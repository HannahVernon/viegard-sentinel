namespace Viegard.Domain.Events;

/// <summary>MDaemon log family parsed from a configured log file.</summary>
public enum MDaemonLogKind
{
    SmtpIn,
    SmtpOut,
    Imap,
    Pop3,
    Screening,
    DynamicScreening,
    Other,
}

/// <summary>High-level MDaemon event classification used by deterministic rules.</summary>
public enum MDaemonEventKind
{
    ConnectionAccepted,
    AuthenticationFailed,
    IpBlocked,
    AccessRefused,
    ScreeningBlocked,
    SessionLine,
    Other,
}

/// <summary>
/// Normalized MDaemon log event.  Every string originates from mail-server
/// logs and remains untrusted observed data.
/// </summary>
public sealed record MDaemonLogEvent : EventPayload
{
    public required MDaemonLogKind LogKind { get; init; }

    public required MDaemonEventKind EventKind { get; init; }

    public string? RemoteIp { get; init; }

    public int? Port { get; init; }

    public string? Reason { get; init; }

    public string? SessionId { get; init; }

    /// <summary>Raw log line text.  Treat as untrusted observed data.</summary>
    public required string Message { get; init; }

    public DateTimeOffset? ReportedAt { get; init; }
}
