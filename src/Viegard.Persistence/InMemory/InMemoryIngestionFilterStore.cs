using System.Collections.Concurrent;
using Viegard.Application.Configuration;
using Viegard.Application.Stores;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryIngestionFilterStore : IIngestionFilterStore
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<IngestionFilterKey, IngestionFilter> _filters = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<IReadOnlyList<IngestionFilter>> ListAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<IngestionFilter>>(_filters.Values
            .OrderBy(f => f.SourceType, StringComparer.Ordinal)
            .ThenBy(f => f.EventKind, StringComparer.Ordinal)
            .ToList());

    public ValueTask<IReadOnlyList<IngestionFilter>> ListForSourceAsync(
        string sourceType,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourceType = IngestionFilterValidation.NormalizeSourceType(sourceType);
        return ValueTask.FromResult<IReadOnlyList<IngestionFilter>>(_filters.Values
            .Where(f => string.Equals(
                IngestionFilterValidation.NormalizeSourceType(f.SourceType),
                normalizedSourceType,
                StringComparison.Ordinal))
            .OrderBy(f => f.EventKind, StringComparer.Ordinal)
            .ToList());
    }

    public ValueTask<IngestionFilterSaveResult> SaveMatrixAsync(
        string sourceType,
        IReadOnlyDictionary<string, bool> suppressions,
        IReadOnlySet<string> lockedEventKinds,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        var rows = IngestionFilterValidation.BuildRows(sourceType, suppressions, lockedEventKinds, updatedBy, updatedAt);
        var normalizedSourceType = IngestionFilterValidation.NormalizeSourceType(sourceType);

        lock (_sync)
        {
            var before = SourceRows(normalizedSourceType);
            foreach (var row in rows)
            {
                _filters[new IngestionFilterKey(row.SourceType, row.EventKind)] = row;
            }

            SignalChanged();
            return ValueTask.FromResult(new IngestionFilterSaveResult(before, SourceRows(normalizedSourceType)));
        }
    }

    public ValueTask SeedDefaultsIfMissingAsync(DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        var inserted = false;
        lock (_sync)
        {
            foreach (var seed in MDaemonIngestionFilterPolicy.DefaultFilters(updatedAt))
            {
                inserted |= _filters.TryAdd(new IngestionFilterKey(seed.SourceType, seed.EventKind), seed);
            }

            if (inserted)
            {
                SignalChanged();
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<long> waiter;
        lock (_sync)
        {
            var current = CurrentChangeVersion;
            if (current != lastSeenVersion)
            {
                return current;
            }

            waiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = timeoutCts.Token.Register(() => waiter.TrySetResult(CurrentChangeVersion));
        timeoutCts.CancelAfter(timeout);
        var result = await waiter.Task.ConfigureAwait(false);

        lock (_sync)
        {
            _waiters.Remove(waiter);
        }

        return result;
    }

    private IReadOnlyList<IngestionFilter> SourceRows(string sourceType) =>
        _filters.Values
            .Where(f => string.Equals(f.SourceType, sourceType, StringComparison.Ordinal))
            .OrderBy(f => f.EventKind, StringComparer.Ordinal)
            .ToList();

    private void SignalChanged()
    {
        var version = Interlocked.Increment(ref _changeVersion);
        foreach (var waiter in _waiters.ToArray())
        {
            waiter.TrySetResult(version);
        }
    }
}
