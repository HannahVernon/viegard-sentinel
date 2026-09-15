using Viegard.Application.Stores;
using Viegard.Domain.Events;

namespace Viegard.Application.Configuration;

public sealed class IngestionFilterSource(
    IIngestionFilterStore filterStore,
    IIngestionFilterDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly IIngestionFilterDiagnostics _diagnostics = diagnostics ?? NullIngestionFilterDiagnostics.Instance;
    private IngestionFilterSnapshot _snapshot = IngestionFilterSnapshot.Empty;

    public bool ShouldEmit(NormalizedEvent normalizedEvent)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);
        var eventKind = EventKindName(normalizedEvent.Payload);
        if (eventKind is null)
        {
            return true;
        }

        var key = new IngestionFilterKey(
            IngestionFilterValidation.NormalizeSourceType(normalizedEvent.SourceType),
            eventKind);
        return !Volatile.Read(ref _snapshot).IsSuppressed(key);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await filterStore.ListAsync(cancellationToken).ConfigureAwait(false);
            var suppressed = new List<IngestionFilterKey>();
            foreach (var row in rows)
            {
                if (!IngestionFilterValidation.TryCreateKey(row, out var key, out var reason))
                {
                    _diagnostics.InvalidFilterSkipped(row, reason);
                    continue;
                }

                if (row.Suppressed)
                {
                    suppressed.Add(key);
                }
            }

            Interlocked.Exchange(ref _snapshot, new IngestionFilterSnapshot(suppressed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.RefreshFailed(ex);
        }
    }

    public async Task RunRefreshLoopAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var seenVersion = filterStore.CurrentChangeVersion;

        while (!cancellationToken.IsCancellationRequested)
        {
            seenVersion = await filterStore.WaitForChangeAsync(seenVersion, RefreshInterval, cancellationToken)
                .ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? EventKindName(EventPayload payload) => payload switch
    {
        MDaemonLogEvent mdaemon => mdaemon.EventKind.ToString(),
        _ => null,
    };

    private sealed class IngestionFilterSnapshot(IEnumerable<IngestionFilterKey> suppressed)
    {
        public static IngestionFilterSnapshot Empty { get; } = new([]);

        private readonly HashSet<IngestionFilterKey> _suppressed = suppressed.ToHashSet();

        public bool IsSuppressed(IngestionFilterKey key) => _suppressed.Contains(key);
    }
}
