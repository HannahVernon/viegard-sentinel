namespace Viegard.Domain.Feedback;

/// <summary>
/// A human correction of a classification (e.g., AI said spam, Hannah said
/// ham).  Stored separately from the original classification so accuracy can
/// be evaluated and thresholds/prompts tuned later.
/// </summary>
public sealed record Correction
{
    public required Guid Id { get; init; }

    public required Guid ClassificationId { get; init; }

    /// <summary>The category the human says is correct.</summary>
    public required string CorrectedCategory { get; init; }

    public required string CorrectedBy { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
