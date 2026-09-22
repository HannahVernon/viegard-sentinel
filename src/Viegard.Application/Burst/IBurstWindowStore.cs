namespace Viegard.Application.Burst;

/// <summary>
/// Durable, per-(signal, source) sliding-window occurrence store.  Backs the
/// burst detector's counting so state survives restarts and can be shared across
/// instances.  Implementations prune occurrences older than the supplied window.
/// </summary>
public interface IBurstWindowStore
{
    /// <summary>
    /// Records one occurrence and returns the current count within the window
    /// ending at <paramref name="occurredAt"/>, along with the contributing event
    /// ids (newest-first, capped at <paramref name="maxEventIds"/>).
    /// </summary>
    ValueTask<BurstWindowState> RecordAndCountAsync(
        string signalId,
        string sourceKey,
        Guid eventId,
        DateTimeOffset occurredAt,
        TimeSpan window,
        int maxEventIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records that a proposal fired for the (signal, source) at
    /// <paramref name="firedAt"/> when no cooldown is active, returning true when
    /// firing is permitted.  Returns false when a prior firing is still within
    /// <paramref name="cooldown"/>, so callers suppress duplicate proposals.
    /// </summary>
    ValueTask<bool> TryBeginCooldownAsync(
        string signalId,
        string sourceKey,
        DateTimeOffset firedAt,
        TimeSpan cooldown,
        CancellationToken cancellationToken = default);
}

/// <summary>Count and contributing events for a (signal, source) window.</summary>
public sealed record BurstWindowState(
    int Count,
    IReadOnlyList<Guid> EventIds,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);
