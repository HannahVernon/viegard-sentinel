using Viegard.Domain.Events;

namespace Viegard.Application.Sources;

/// <summary>
/// One observation plus its raw payload, as produced by a data source.  The
/// ingestion pipeline persists both; sources never touch persistence.
/// </summary>
public sealed record ObservedItem
{
    public required RawObservation Observation { get; init; }

    /// <summary>The raw payload (source-specific serialization).  Untrusted observed data.</summary>
    public required string RawPayload { get; init; }
}
