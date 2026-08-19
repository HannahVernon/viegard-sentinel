namespace Viegard.Domain.Events;

/// <summary>
/// Base type for source-specific normalized event payloads (e.g.,
/// <see cref="MailMessageEvent"/>).  Payload records live in the domain so
/// core components can use them, but must remain plain data with no
/// dependency on any source library.  The core pipeline treats payloads
/// polymorphically.
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
