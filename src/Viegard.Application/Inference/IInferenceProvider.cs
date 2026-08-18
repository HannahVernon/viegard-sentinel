namespace Viegard.Application.Inference;

/// <summary>
/// Trust level of a prompt variable.  The prompt assembler renders each level
/// into clearly delimited prompt sections; untrusted observed data can never
/// redefine the model's role or available actions.
/// </summary>
public enum PromptTrust
{
    /// <summary>Viegard's own system instructions.</summary>
    System,

    /// <summary>Application-supplied context (template-controlled, not observed).</summary>
    Application,

    /// <summary>Observed data (email content, log lines, URLs, User-Agents).  Always treated as data, never instructions.</summary>
    UntrustedObservedData,
}

/// <summary>A named variable supplied to a prompt template, tagged with its trust level.</summary>
public sealed record PromptVariable
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public required PromptTrust Trust { get; init; }
}

/// <summary>
/// Provider-neutral structured inference request.  No provider-specific or
/// OpenAI-specific types may appear here (D-0002); adapters translate to
/// their wire protocol.
/// </summary>
public sealed record InferenceRequest
{
    /// <summary>Identifier of the versioned prompt template to use.</summary>
    public required string TemplateId { get; init; }

    public required IReadOnlyList<PromptVariable> Variables { get; init; }

    /// <summary>Identifier of the JSON schema the output must validate against.</summary>
    public required string OutputSchemaId { get; init; }

    public int? MaxTokens { get; init; }

    public double? Temperature { get; init; }

    public TimeSpan? Timeout { get; init; }
}

/// <summary>Why an inference attempt failed.</summary>
public enum InferenceFailureKind
{
    Unavailable,
    Timeout,
    MalformedOutput,
    Overloaded,
    ResponseTooLarge,
    Cancelled,
    Unknown,
}

/// <summary>
/// Result of an inference attempt.  RawOutput is unvalidated model output and
/// must pass schema validation before any downstream use.
/// </summary>
public sealed record InferenceResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Unvalidated model output; treat as untrusted until schema-validated.</summary>
    public string? RawOutput { get; init; }

    public InferenceFailureKind? FailureKind { get; init; }

    public string? FailureDetail { get; init; }

    /// <summary>Model identity as reported by the backend, for audit records.</summary>
    public string? ModelId { get; init; }

    public TimeSpan? Latency { get; init; }

    public static InferenceResult Success(string rawOutput, string? modelId, TimeSpan? latency) =>
        new() { Succeeded = true, RawOutput = rawOutput, ModelId = modelId, Latency = latency };

    public static InferenceResult Failure(InferenceFailureKind kind, string? detail = null) =>
        new() { Succeeded = false, FailureKind = kind, FailureDetail = detail };
}

/// <summary>
/// Provider-neutral local inference port (Mind).  Implementations adapt
/// llama.cpp, Ollama, vLLM, or other backends.  The application never knows
/// which model or runtime is behind this interface, and it must keep
/// functioning when the provider is unavailable.
/// </summary>
public interface IInferenceProvider
{
    string ProviderId { get; }

    Task<InferenceResult> InferAsync(InferenceRequest request, CancellationToken cancellationToken = default);
}
