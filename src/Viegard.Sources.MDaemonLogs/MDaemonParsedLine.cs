using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs;

public sealed record MDaemonParsedLine
{
    public required MDaemonLogKind LogKind { get; init; }

    public required MDaemonEventKind EventKind { get; init; }

    public string? RemoteIp { get; init; }

    public int? Port { get; init; }

    public string? Reason { get; init; }

    public string? SessionId { get; init; }

    public required string Message { get; init; }

    public DateTimeOffset? ReportedAt { get; init; }

    public bool IsNoise { get; init; }
}
