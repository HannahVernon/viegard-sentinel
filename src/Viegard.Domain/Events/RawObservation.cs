namespace Viegard.Domain.Events;

/// <summary>
/// An untouched observation produced by a data source (Eyes) before any
/// parsing or normalization.  The raw payload itself is stored separately;
/// this record carries a reference to it.
/// </summary>
public sealed record RawObservation
{
    public required Guid Id { get; init; }

    /// <summary>Identifier of the configured data-source instance that produced this observation.</summary>
    public required string SourceId { get; init; }

    /// <summary>Kind of source that produced this observation (e.g., "imap", "syslog", "mdaemon").</summary>
    public required string SourceType { get; init; }

    /// <summary>When the source observed the data (not when it originally occurred).</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Locator for the stored raw payload (store-specific reference, never the payload itself).</summary>
    public required string PayloadReference { get; init; }

    /// <summary>
    /// Source-specific ingestion offset (e.g., IMAP UID, log byte offset) used to
    /// resume ingestion safely after a restart.
    /// </summary>
    public string? IngestOffset { get; init; }
}
