namespace Viegard.Domain.Events;

/// <summary>
/// Base type for source-specific normalized event payloads.  Concrete payloads
/// (e.g., HTTP request, mail message) are defined alongside their source
/// adapters; the core pipeline treats payloads polymorphically.
/// </summary>
public abstract record EventPayload;

/// <summary>
/// Payload representing a record that could not be parsed.  Malformed input
/// must never crash ingestion; it becomes one of these instead.
/// </summary>
public sealed record MalformedRecordPayload : EventPayload
{
    public required string Reason { get; init; }

    /// <summary>A short, sanitized sample of the malformed input; treat as untrusted.</summary>
    public string? RawSample { get; init; }
}
