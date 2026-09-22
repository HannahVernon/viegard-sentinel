using Microsoft.Extensions.Options;

namespace Viegard.Application.Classifiers;

/// <summary>Configuration for deterministic and future optional classifiers.</summary>
public sealed class ClassifierOptions
{
    public const string SectionName = "Viegard:Classifiers";

    public double ScoreForFullConfidence { get; set; } = 5.0;

    public double SeverityPerScorePoint { get; set; } = 2.0;

    public double BlockRecommendationScore { get; set; } = 3.0;

    public int RepeatConfidenceMinEvents { get; set; } = 4;

    public double RepeatConfidenceCoefficient { get; set; } = 0.08;

    public double RepeatConfidenceBonusCap { get; set; } = 0.30;
}

/// <summary>Startup validation for classifier configuration.</summary>
public sealed class ClassifierOptionsValidator : IValidateOptions<ClassifierOptions>
{
    public ValidateOptionsResult Validate(string? name, ClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidatePositive(options.ScoreForFullConfidence, nameof(options.ScoreForFullConfidence), failures);
        ValidatePositive(options.SeverityPerScorePoint, nameof(options.SeverityPerScorePoint), failures);
        ValidatePositive(options.BlockRecommendationScore, nameof(options.BlockRecommendationScore), failures);
        if (options.RepeatConfidenceMinEvents < 1)
        {
            failures.Add($"Classifier {nameof(options.RepeatConfidenceMinEvents)} must be at least 1.");
        }

        if (options.RepeatConfidenceCoefficient < 0.0
            || double.IsNaN(options.RepeatConfidenceCoefficient)
            || double.IsInfinity(options.RepeatConfidenceCoefficient))
        {
            failures.Add($"Classifier {nameof(options.RepeatConfidenceCoefficient)} must be non-negative and finite.");
        }

        if (options.RepeatConfidenceBonusCap is < 0.0 or > 1.0
            || double.IsNaN(options.RepeatConfidenceBonusCap))
        {
            failures.Add($"Classifier {nameof(options.RepeatConfidenceBonusCap)} must be within [0.0, 1.0].");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidatePositive(double value, string name, List<string> failures)
    {
        if (value <= 0.0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            failures.Add($"Classifier {name} must be positive.");
        }
    }
}
