using Viegard.Domain.Classifications;

namespace Viegard.Application.Policy;

public sealed record PolicyThresholdSettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const string SystemSeedActor = "system:policy-threshold-seed";

    public int Id { get; init; } = FixedId;

    public double ReviewConfidence { get; init; } = 0.7;

    public double ActionConfidence { get; init; } = 0.9;

    public int ActionMinSeverity { get; init; } = 7;

    public int RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public PolicyThresholdValues ToValues() => new(ReviewConfidence, ActionConfidence, ActionMinSeverity);

    public static PolicyThresholdSettings FromOptions(PolicyOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new PolicyThresholdSettings
        {
            Id = FixedId,
            ReviewConfidence = options.AiReviewConfidence,
            ActionConfidence = options.AiActionConfidence,
            ActionMinSeverity = options.AiActionMinSeverity,
            RowVersion = 1,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public sealed record PolicyThresholdValues(
    double ReviewConfidence,
    double ActionConfidence,
    int ActionMinSeverity)
{
    public static PolicyThresholdValues FromOptions(PolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PolicyThresholdValues(
            options.AiReviewConfidence,
            options.AiActionConfidence,
            options.AiActionMinSeverity);
    }
}

public static class PolicyThresholdSettingsValidator
{
    public const string ConfidenceBoundsError = "Policy threshold confidences must be within [0.0, 1.0].";
    public const string ConfidenceOrderError = "Policy threshold review confidence must be less than or equal to action confidence.";

    public static readonly string SeverityRangeError =
        $"Policy threshold minimum severity must be between {Classification.MinSeverity} and {Classification.MaxSeverity}.";

    public static bool TryValidate(PolicyThresholdSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!IsValidConfidence(settings.ReviewConfidence) || !IsValidConfidence(settings.ActionConfidence))
        {
            error = ConfidenceBoundsError;
            return false;
        }

        if (settings.ReviewConfidence > settings.ActionConfidence)
        {
            error = ConfidenceOrderError;
            return false;
        }

        if (settings.ActionMinSeverity is < Classification.MinSeverity or > Classification.MaxSeverity)
        {
            error = SeverityRangeError;
            return false;
        }

        if (settings.Id != PolicyThresholdSettings.FixedId)
        {
            error = "Policy threshold settings row has an invalid id.";
            return false;
        }

        if (settings.RowVersion < 0)
        {
            error = "Policy threshold settings row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= PolicyThresholdSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..PolicyThresholdSettings.MaxUpdatedByLength];
    }

    private static bool IsValidConfidence(double value) =>
        value is >= 0.0 and <= 1.0 && !double.IsNaN(value);
}

public interface IPolicyThresholdSettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<PolicyThresholdSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<PolicyThresholdSettingsCreateResult> TryCreateAsync(
        PolicyThresholdSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<PolicyThresholdSettingsSaveResult> UpdateAsync(
        PolicyThresholdSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record PolicyThresholdSettingsCreateResult(bool Created, PolicyThresholdSettings Settings);

public enum PolicyThresholdSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record PolicyThresholdSettingsSaveResult(
    PolicyThresholdSettingsSaveStatus Status,
    PolicyThresholdSettings? Settings)
{
    public bool Succeeded => Status == PolicyThresholdSettingsSaveStatus.Saved;

    public static PolicyThresholdSettingsSaveResult Saved(PolicyThresholdSettings settings) =>
        new(PolicyThresholdSettingsSaveStatus.Saved, settings);

    public static PolicyThresholdSettingsSaveResult Conflict(PolicyThresholdSettings? current) =>
        new(PolicyThresholdSettingsSaveStatus.Conflict, current);
}
