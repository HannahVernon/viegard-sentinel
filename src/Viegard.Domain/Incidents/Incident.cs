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
}
