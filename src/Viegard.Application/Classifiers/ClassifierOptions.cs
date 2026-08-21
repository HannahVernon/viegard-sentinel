using Microsoft.Extensions.Options;

namespace Viegard.Application.Classifiers;

/// <summary>Configuration for deterministic and future optional classifiers.</summary>
public sealed class ClassifierOptions
{
    public const string SectionName = "Viegard:Classifiers";

    public double ScoreForFullConfidence { get; set; } = 5.0;

    public double SeverityPerScorePoint { get; set; } = 2.0;

    public double BlockRecommendationScore { get; set; } = 3.0;
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
