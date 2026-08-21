namespace Viegard.Domain.Events;

/// <summary>
/// Normalized event payload for an observed HTTP request (nginx/SWAG access
/// logs).  Every string is untrusted observed data; the request line and
/// User-Agent in particular are attacker-controlled.
/// </summary>
public sealed record HttpRequestEvent : EventPayload
{
    /// <summary>Client address as reported by the log line.</summary>
    public required string RemoteAddress { get; init; }

    public string? RemoteUser { get; init; }

    public DateTimeOffset? RequestedAt { get; init; }

    public string? Method { get; init; }

    public string? Uri { get; init; }

    public string? Protocol { get; init; }

    public int? StatusCode { get; init; }

    public long? BodyBytes { get; init; }

    public string? Referrer { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Virtual host ($host) when the log format includes it.</summary>
    public string? Host { get; init; }

    /// <summary>Request duration in seconds when the log format includes it.</summary>
    public double? RequestSeconds { get; init; }
}

/// <summary>
/// Generic normalized payload for a syslog message that no specific parser
/// claimed.  The claimed hostname and tag come from the datagram and are
/// untrusted; origin identity is the peer IP.
/// </summary>
public sealed record SyslogEvent : EventPayload
{
    /// <summary>The datagram sender (transport-level identity).</summary>
    public required string PeerIp { get; init; }

    public int? Facility { get; init; }

    public int? Severity { get; init; }

    /// <summary>Hostname claimed inside the message.  Untrusted.</summary>
    public string? ClaimedHostname { get; init; }

    public string? Tag { get; init; }

    public required string Message { get; init; }

    public DateTimeOffset? ReportedAt { get; init; }
}
