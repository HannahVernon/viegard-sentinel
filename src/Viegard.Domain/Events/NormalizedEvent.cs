namespace Viegard.Domain.Events;

/// <summary>
/// The single normalized event representation shared by all sources, so mail,
/// web, and future events correlate without incompatible per-source models.
/// </summary>
public sealed record NormalizedEvent
{
    public required Guid Id { get; init; }

    /// <summary>Identifier of the configured data-source instance.</summary>
    public required string SourceId { get; init; }

    /// <summary>Source family (e.g., "imap", "nginx", "mdaemon").</summary>
    public required string SourceType { get; init; }

    /// <summary>When the event originally occurred, as reported by the source.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Entities involved in the event (IPs, addresses, hosts, ...).  Untrusted input.</summary>
    public required IReadOnlyList<EntityRef> Entities { get; init; }

    /// <summary>Source-specific typed payload.  Untrusted input.</summary>
    public required EventPayload Payload { get; init; }

    /// <summary>The raw observation this event was normalized from.</summary>
    public required Guid RawObservationId { get; init; }
}
