using System.Text.Json.Serialization;

namespace Viegard.Domain.Events;

/// <summary>
/// Base type for source-specific normalized event payloads (e.g.,
/// <see cref="MailMessageEvent"/>).  Payload records live in the domain so
/// core components can use them, but must remain plain data with no
/// dependency on any source library.  The core pipeline treats payloads
/// polymorphically.  The JSON polymorphism attributes (BCL-only) give
/// payloads a stable persisted discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$payloadType")]
[JsonDerivedType(typeof(MalformedRecordPayload), "malformed-record")]
[JsonDerivedType(typeof(MailMessageEvent), "mail-message")]
[JsonDerivedType(typeof(HttpRequestEvent), "http-request")]
[JsonDerivedType(typeof(SyslogEvent), "syslog")]
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
