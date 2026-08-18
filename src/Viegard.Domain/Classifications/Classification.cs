namespace Viegard.Domain.Classifications;

/// <summary>What a classification is about.</summary>
public enum ClassificationSubjectKind
{
    Incident,
    MailMessage,
}

/// <summary>Identifies the model that produced an AI-backed classification.</summary>
public sealed record ModelInfo
{
    public required string ModelId { get; init; }

    public string? ModelVersion { get; init; }

    /// <summary>Version of the prompt template used, for audit reproducibility.</summary>
    public string? PromptTemplateVersion { get; init; }
}

/// <summary>
/// The structured, validated outcome of a classifier (Mind).  A classification
/// is always a recommendation; only the policy engine (Judgment) decides.
/// </summary>
public sealed record Classification
{
    public const int MinSeverity = 0;
    public const int MaxSeverity = 10;

    private readonly double _confidence;
    private readonly int _severity;
    private readonly double? _uncertainty;

    public required Guid Id { get; init; }

    public required ClassificationSubjectKind SubjectKind { get; init; }

    /// <summary>The incident or message this classification applies to.</summary>
    public required Guid SubjectId { get; init; }

    /// <summary>Identifier of the classifier implementation that produced this result.</summary>
    public required string ClassifierId { get; init; }

    /// <summary>Model details when the classifier is AI-backed; null for deterministic classifiers.</summary>
    public ModelInfo? Model { get; init; }

    /// <summary>Configured category label (e.g., "spam", "vulnerability-scanner").</summary>
    public required string Category { get; init; }

    /// <summary>Classifier confidence in the range [0.0, 1.0].</summary>
    public required double Confidence
    {
        get => _confidence;
        init => _confidence = value is >= 0.0 and <= 1.0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Confidence), value, "Confidence must be within [0.0, 1.0].");
    }

    /// <summary>Severity in the range [0, 10].</summary>
    public required int Severity
    {
        get => _severity;
        init => _severity = value is >= MinSeverity and <= MaxSeverity
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Severity), value, "Severity must be within [0, 10].");
    }

    /// <summary>Human-readable reasons supporting the classification.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>The action the classifier recommends; never executed directly.</summary>
    public string? RecommendedAction { get; init; }

    /// <summary>Optional self-reported uncertainty in the range [0.0, 1.0].</summary>
    public double? Uncertainty
    {
        get => _uncertainty;
        init => _uncertainty = value is null or (>= 0.0 and <= 1.0)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Uncertainty), value, "Uncertainty must be within [0.0, 1.0].");
    }

    public required DateTimeOffset CreatedAt { get; init; }
}
