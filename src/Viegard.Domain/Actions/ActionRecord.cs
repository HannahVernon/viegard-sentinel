namespace Viegard.Domain.Actions;

public enum ActionStatus
{
    Pending,
    Succeeded,
    Failed,
    RolledBack,

    /// <summary>The action was evaluated in dry-run mode and never executed.</summary>
    DryRun,
}

/// <summary>
/// A record of one action execution (Talons).  Providers expose a closed
/// catalog of typed operations; this record captures which operation ran,
/// with what parameters, and how to undo it.
/// </summary>
public sealed record ActionRecord
{
    public required Guid Id { get; init; }

    /// <summary>The policy decision that authorized this action.</summary>
    public required Guid DecisionId { get; init; }

    /// <summary>The action provider (e.g., "imap", "mikrotik").</summary>
    public required string ProviderId { get; init; }

    /// <summary>The typed operation within the provider's catalog (e.g., "move-message").</summary>
    public required string OperationId { get; init; }

    /// <summary>Serialized, validated operation parameters.  Never contains secrets.</summary>
    public string? ParametersJson { get; init; }

    public required ActionStatus Status { get; init; }

    public string? Error { get; init; }

    /// <summary>Serialized information sufficient to reverse the action, when reversible.</summary>
    public string? RollbackJson { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}
