namespace Viegard.Application.Classifiers;

public sealed record ClassifierSettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const string SystemSeedActor = "system:classifier-settings-seed";

    public int Id { get; init; } = FixedId;

    public double ScoreForFullConfidence { get; init; } = 5.0;

    public double SeverityPerScorePoint { get; init; } = 2.0;

    public double BlockRecommendationScore { get; init; } = 3.0;

    public int RepeatConfidenceMinEvents { get; init; } = 4;

    public double RepeatConfidenceCoefficient { get; init; } = 0.08;

    public double RepeatConfidenceBonusCap { get; init; } = 0.30;

    public int RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public ClassifierValues ToValues() => new(
        ScoreForFullConfidence,
        SeverityPerScorePoint,
        BlockRecommendationScore,
        RepeatConfidenceMinEvents,
        RepeatConfidenceCoefficient,
        RepeatConfidenceBonusCap);

    public static ClassifierSettings FromOptions(ClassifierOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new ClassifierSettings
        {
            Id = FixedId,
            ScoreForFullConfidence = options.ScoreForFullConfidence,
            SeverityPerScorePoint = options.SeverityPerScorePoint,
            BlockRecommendationScore = options.BlockRecommendationScore,
            RepeatConfidenceMinEvents = options.RepeatConfidenceMinEvents,
            RepeatConfidenceCoefficient = options.RepeatConfidenceCoefficient,
            RepeatConfidenceBonusCap = options.RepeatConfidenceBonusCap,
            RowVersion = 1,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public sealed record ClassifierValues(
    double ScoreForFullConfidence,
    double SeverityPerScorePoint,
    double BlockRecommendationScore,
    int RepeatConfidenceMinEvents,
    double RepeatConfidenceCoefficient,
    double RepeatConfidenceBonusCap)
{
    public static ClassifierValues FromOptions(ClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ClassifierValues(
            options.ScoreForFullConfidence,
            options.SeverityPerScorePoint,
            options.BlockRecommendationScore,
            options.RepeatConfidenceMinEvents,
            options.RepeatConfidenceCoefficient,
            options.RepeatConfidenceBonusCap);
    }
}

public static class ClassifierSettingsValidator
{
    public const string PositiveScoreError = "Classifier score controls must be positive and finite.";
    public const string RepeatMinEventsError = "Classifier repeat-confidence minimum events must be at least 1.";
    public const string RepeatCoefficientError = "Classifier repeat-confidence coefficient must be non-negative and finite.";
    public const string RepeatBonusCapError = "Classifier repeat-confidence bonus cap must be within [0.0, 1.0].";

    public static bool TryValidate(ClassifierSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!IsPositiveFinite(settings.ScoreForFullConfidence)
            || !IsPositiveFinite(settings.SeverityPerScorePoint)
            || !IsPositiveFinite(settings.BlockRecommendationScore))
        {
            error = PositiveScoreError;
            return false;
        }

        if (settings.RepeatConfidenceMinEvents < 1)
        {
            error = RepeatMinEventsError;
            return false;
        }

        if (settings.RepeatConfidenceCoefficient < 0.0
            || !double.IsFinite(settings.RepeatConfidenceCoefficient))
        {
            error = RepeatCoefficientError;
            return false;
        }

        if (settings.RepeatConfidenceBonusCap is < 0.0 or > 1.0
            || double.IsNaN(settings.RepeatConfidenceBonusCap))
        {
            error = RepeatBonusCapError;
            return false;
        }

        if (settings.Id != ClassifierSettings.FixedId)
        {
            error = "Classifier settings row has an invalid id.";
            return false;
        }

        if (settings.RowVersion < 0)
        {
            error = "Classifier settings row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= ClassifierSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..ClassifierSettings.MaxUpdatedByLength];
    }

    private static bool IsPositiveFinite(double value) =>
        value > 0.0 && double.IsFinite(value);
}

public interface IClassifierSettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<ClassifierSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<ClassifierSettingsCreateResult> TryCreateAsync(
        ClassifierSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<ClassifierSettingsSaveResult> UpdateAsync(
        ClassifierSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record ClassifierSettingsCreateResult(bool Created, ClassifierSettings Settings);

public enum ClassifierSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record ClassifierSettingsSaveResult(
    ClassifierSettingsSaveStatus Status,
    ClassifierSettings? Settings)
{
    public bool Succeeded => Status == ClassifierSettingsSaveStatus.Saved;

    public static ClassifierSettingsSaveResult Saved(ClassifierSettings settings) =>
        new(ClassifierSettingsSaveStatus.Saved, settings);

    public static ClassifierSettingsSaveResult Conflict(ClassifierSettings? current) =>
        new(ClassifierSettingsSaveStatus.Conflict, current);
}
