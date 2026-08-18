namespace Viegard.Application.Inference.Prompts;

/// <summary>
/// A versioned prompt template.  Placeholders of the form {name} may appear
/// in the system and application sections and are substituted only from
/// System- or Application-trust variables.  Untrusted observed data can never
/// be substituted into template text; it is appended exclusively inside
/// random-boundary data blocks by the assembler.
/// </summary>
public sealed record PromptTemplate
{
    public required string TemplateId { get; init; }

    /// <summary>Template version, recorded in audit records for reproducibility.</summary>
    public required string Version { get; init; }

    /// <summary>Viegard's system instructions (role, task, output schema contract).</summary>
    public required string SystemInstructions { get; init; }

    /// <summary>Application-supplied context and task framing.</summary>
    public required string ApplicationInstructions { get; init; }
}
