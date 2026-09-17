namespace Viegard.Domain.Decisions;

/// <summary>Outcome of policy evaluation (Judgment).</summary>
public enum DecisionOutcome
{
    /// <summary>The policy engine authorized an action to be executed.</summary>
    ActionAuthorized = 0,

    /// <summary>The policy engine recorded the decision without authorizing an action.</summary>
    RecordOnly = 1,

    /// <summary>The action requires manual operator approval before execution.</summary>
    RequireApproval = 2,

    /// <summary>Dry-run: the action is recorded as "would execute" but not performed.</summary>
    DryRun = 3,
}

/// <summary>Manual operator review outcome for an approval-required decision.</summary>
public enum DecisionReviewOutcome
{
    Approved,
    Rejected,
}

/// <summary>The result of one guardrail check during policy evaluation.</summary>
public sealed record GuardrailEvaluation
{
    public required string GuardrailName { get; init; }

    public required bool Passed { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// The auditable result of evaluating a classification against policy.  The
/// policy engine is the only component that authorizes actions.
/// </summary>
public sealed record Decision
{
    public required Guid Id { get; init; }

    public required Guid ClassificationId { get; init; }

    public required string PolicyId { get; init; }

    public required string PolicyVersion { get; init; }

    public required DecisionOutcome Outcome { get; init; }

    public required string Rationale { get; init; }

    /// <summary>Every guardrail evaluated, pass or fail, for explainability.</summary>
    public required IReadOnlyList<GuardrailEvaluation> Guardrails { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? ReviewedBy { get; init; }

    public DateTimeOffset? ReviewedAt { get; init; }

    public DecisionReviewOutcome? ReviewOutcome { get; init; }
}
