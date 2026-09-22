using System.Collections.Concurrent;
using Viegard.Application.Burst;

namespace Viegard.Persistence.InMemory;

/// <summary>
/// In-memory (source, signal) sliding-window occurrence store.  Per-process only;
/// the Postgres implementation provides durable, multi-instance state.
/// </summary>
public sealed class InMemoryBurstWindowStore : IBurstWindowStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Occurrence>> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFired = new(StringComparer.Ordinal);

    public ValueTask<BurstWindowState> RecordAndCountAsync(
        string signalId,
        string sourceKey,
        Guid eventId,
        DateTimeOffset occurredAt,
        TimeSpan window,
        int maxEventIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var key = Key(signalId, sourceKey);
        var occurredUtc = occurredAt.ToUniversalTime();
        var floor = occurredUtc - window;

        lock (_sync)
        {
            if (!_windows.TryGetValue(key, out var occurrences))
            {
                occurrences = [];
                _windows[key] = occurrences;
            }

            occurrences.Add(new Occurrence(eventId, occurredUtc));
            occurrences.RemoveAll(o => o.OccurredAt < floor);

            var windowStart = occurrences.Count == 0 ? occurredUtc : occurrences.Min(o => o.OccurredAt);
            var eventIds = occurrences
                .OrderByDescending(o => o.OccurredAt)
                .Take(Math.Max(1, maxEventIds))
                .Select(o => o.EventId)
                .ToList();

            return ValueTask.FromResult(new BurstWindowState(
                occurrences.Count,
                eventIds,
                windowStart,
                occurredUtc));
        }
    }

    public ValueTask<bool> TryBeginCooldownAsync(
        string signalId,
        string sourceKey,
        DateTimeOffset firedAt,
        TimeSpan cooldown,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var key = Key(signalId, sourceKey);
        var firedUtc = firedAt.ToUniversalTime();

        lock (_sync)
        {
            if (_lastFired.TryGetValue(key, out var last) && firedUtc < last + cooldown)
            {
                return ValueTask.FromResult(false);
            }

            _lastFired[key] = firedUtc;
            return ValueTask.FromResult(true);
        }
    }

    private static string Key(string signalId, string sourceKey) => signalId + "\u0000" + sourceKey;

    private readonly record struct Occurrence(Guid EventId, DateTimeOffset OccurredAt);
}
