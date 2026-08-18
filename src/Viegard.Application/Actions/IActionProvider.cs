using Viegard.Domain.Actions;

namespace Viegard.Application.Actions;

/// <summary>Describes one operation in a provider's closed catalog.</summary>
public sealed record ActionOperationDescriptor
{
    public required string OperationId { get; init; }

    public required string Description { get; init; }

    /// <summary>True when the operation modifies external state destructively (e.g., delete mail).  Destructive operations ship disabled.</summary>
    public required bool Destructive { get; init; }

    /// <summary>True when the operation can be reversed via rollback information.</summary>
    public required bool Reversible { get; init; }
}

/// <summary>A validated request to execute one cataloged operation.</summary>
public sealed record ActionRequest
{
    public required Guid DecisionId { get; init; }

    public required string ProviderId { get; init; }

    public required string OperationId { get; init; }

    /// <summary>Serialized parameters; providers validate before execution.  Never contains secrets.</summary>
    public string? ParametersJson { get; init; }
}

/// <summary>
/// An action provider (Talons).  Providers expose a closed catalog of typed
/// operations, never command strings, and validate all inputs (IP syntax,
/// protected ranges, durations) as defense in depth: policy has already
/// authorized the request, but providers re-check their own invariants.
/// </summary>
public interface IActionProvider
{
    string ProviderId { get; }

    IReadOnlyList<ActionOperationDescriptor> SupportedOperations { get; }

    Task<ActionRecord> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default);
}
