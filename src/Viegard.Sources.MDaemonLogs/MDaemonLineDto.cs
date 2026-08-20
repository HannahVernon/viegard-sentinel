using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs;

/// <summary>
/// Serialization contract between the file tailer and normalizer.  The line
/// text and file name are untrusted observed data.
/// </summary>
public sealed record MDaemonLineDto
{
    public int SchemaVersion { get; init; }

    public MDaemonLogKind? LogKind { get; init; }

    public string? FileName { get; init; }

    public string? LineText { get; init; }

    public DateTimeOffset CapturedAt { get; init; }
}
