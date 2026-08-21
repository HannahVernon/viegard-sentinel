namespace Viegard.Domain.Incidents;

/// <summary>A scored piece of evidence contributing to an incident.</summary>
public sealed record EvidenceItem
{
    public required string Description { get; init; }

    /// <summary>Relative evidence weight assigned by deterministic detection.</summary>
    public required double Score { get; init; }

    /// <summary>The normalized event this evidence derives from, when applicable.</summary>
    public Guid? EventId { get; init; }
}
