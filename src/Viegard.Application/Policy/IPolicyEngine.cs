using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;

namespace Viegard.Application.Policy;

/// <summary>
/// Context for policy evaluation beyond the classification itself: current
/// guardrail state (rate counters, circuit breaker, emergency stop, dry-run
/// and approval modes) is supplied by the engine's own dependencies.
/// </summary>
public sealed record PolicyContext
{
    /// <summary>True when the platform-wide dry-run mode is active.</summary>
    public required bool DryRun { get; init; }

    /// <summary>True when every action requires manual approval.</summary>
    public required bool ManualApprovalMode { get; init; }

    /// <summary>True when the emergency stop for automated actions is engaged.</summary>
    public required bool EmergencyStop { get; init; }
}

/// <summary>
/// The policy engine (Judgment): the only component that authorizes actions.
/// Deterministic, inspectable, and independent of the LLM.  Guardrails
/// (protected addresses, rate caps, ban-duration limits, cooldowns) are
/// enforced here and cannot be bypassed by any classifier.
/// </summary>
public interface IPolicyEngine
{
    Task<Decision> EvaluateAsync(Classification classification, PolicyContext context, CancellationToken cancellationToken = default);
}
