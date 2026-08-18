namespace Viegard.Domain.Commands;

public enum AdminCommandStatus
{
    Pending,
    InProgress,
    Succeeded,
    Failed,
    Rejected,
}

/// <summary>
/// A durable command submitted by the admin service for the pipeline host to
/// execute (e.g., approve action, unblock IP, retry classification).  The
/// admin service never invokes pipeline behavior directly; commands are the
/// only write path, and each is validated against policy before execution.
/// </summary>
public sealed record AdminCommand
{
    public required Guid Id { get; init; }

    /// <summary>Command kind from a closed, known set (e.g., "approve-action").</summary>
    public required string Kind { get; init; }

    /// <summary>Serialized, validated command parameters.  Never contains secrets.</summary>
    public string? ParametersJson { get; init; }

    /// <summary>Authenticated identity that submitted the command.</summary>
    public required string RequestedBy { get; init; }

    public required AdminCommandStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? Error { get; init; }
}
