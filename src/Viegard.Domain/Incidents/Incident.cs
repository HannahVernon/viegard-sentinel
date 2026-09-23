namespace Viegard.Domain.Incidents;

public enum IncidentState
{
    Open,
    Classified,
    Decided,
    Closed,
}

/// <summary>
/// A correlated episode aggregating related normalized events (Flight), e.g.,
/// all suspicious requests from one IP within a time window.  Incidents, not
/// individual events, are the primary unit of classification.
/// </summary>
public sealed record Incident
{
    public required Guid Id { get; init; }

    /// <summary>The correlation dimensions that grouped these events (e.g., "ip=(value)|window=60s").</summary>
    public required string CorrelationKey { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>Member normalized events.</summary>
    public required IReadOnlyList<Guid> EventIds { get; init; }

    /// <summary>Scored evidence assigned by deterministic detection.</summary>
    public required IReadOnlyList<EvidenceItem> Evidence { get; init; }

    public required IncidentState State { get; init; }

    /// <summary>
    /// While set and in the future, the incident is still absorbing same-key
    /// events (the coalescing window).  Each appended event may extend this,
    /// bounded by a maximum window.  Null for incidents that do not coalesce
    /// (e.g. burst aggregates).  When the deadline passes the finalizer closes
    /// the incident, merging any events that arrived after the provisional
    /// decision into one superseding final decision.
    /// </summary>
    public DateTimeOffset? CoalesceUntil { get; init; }

    /// <summary>
    /// Number of member events covered by the provisional decision.  When the
    /// incident grows beyond this before the window closes, the finalizer emits
    /// a merged decision that supersedes the provisional one.
    /// </summary>
    public int DecidedEventCount { get; init; }
}
