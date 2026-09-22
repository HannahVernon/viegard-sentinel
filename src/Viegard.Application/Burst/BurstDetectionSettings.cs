namespace Viegard.Application.Burst;

/// <summary>
/// Durable, admin-owned configuration for the rate-based burst detector.  A
/// single fixed row (<see cref="FixedId"/>) carries the master switch plus the
/// per-signal tunables.  Additional signals are added as further fields on this
/// row, mirroring the classifier-settings pattern.
/// </summary>
public sealed record BurstDetectionSettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const string SystemSeedActor = "system:burst-detection-settings-seed";

    public int Id { get; init; } = FixedId;

    public bool GlobalEnabled { get; init; } = true;

    public bool AuthFailureEnabled { get; init; } = true;

    public int AuthFailureThreshold { get; init; } = 5;

    public int AuthFailureWindowSeconds { get; init; } = 300;

    public int AuthFailureCooldownSeconds { get; init; } = 3600;

    public bool AuthFailureActionEligible { get; init; }

    public int RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public BurstDetectionValues ToValues() => new(
        GlobalEnabled,
        AuthFailureEnabled,
        AuthFailureThreshold,
        AuthFailureWindowSeconds,
        AuthFailureCooldownSeconds,
        AuthFailureActionEligible);

    public static BurstDetectionSettings FromOptions(BurstDetectionOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new BurstDetectionSettings
        {
            Id = FixedId,
            GlobalEnabled = options.GlobalEnabled,
            AuthFailureEnabled = options.AuthFailureEnabled,
            AuthFailureThreshold = options.AuthFailureThreshold,
            AuthFailureWindowSeconds = options.AuthFailureWindowSeconds,
            AuthFailureCooldownSeconds = options.AuthFailureCooldownSeconds,
            AuthFailureActionEligible = options.AuthFailureActionEligible,
            RowVersion = 1,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public sealed record BurstDetectionValues(
    bool GlobalEnabled,
    bool AuthFailureEnabled,
    int AuthFailureThreshold,
    int AuthFailureWindowSeconds,
    int AuthFailureCooldownSeconds,
    bool AuthFailureActionEligible)
{
    public static BurstDetectionValues FromOptions(BurstDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BurstDetectionValues(
            options.GlobalEnabled,
            options.AuthFailureEnabled,
            options.AuthFailureThreshold,
            options.AuthFailureWindowSeconds,
            options.AuthFailureCooldownSeconds,
            options.AuthFailureActionEligible);
    }

    /// <summary>
    /// Resolves the tunables for a signal id, or null when the signal is not
    /// configured on this row.  New signals extend this switch.
    /// </summary>
    public BurstSignalConfig? ForSignal(string signalId) => signalId switch
    {
        BurstSignalIds.AuthFailure => new BurstSignalConfig(
            AuthFailureEnabled,
            AuthFailureThreshold,
            TimeSpan.FromSeconds(AuthFailureWindowSeconds),
            TimeSpan.FromSeconds(AuthFailureCooldownSeconds),
            AuthFailureActionEligible),
        _ => null,
    };
}

/// <summary>Resolved per-signal tunables handed to the detector engine.</summary>
public sealed record BurstSignalConfig(
    bool Enabled,
    int Threshold,
    TimeSpan Window,
    TimeSpan Cooldown,
    bool ActionEligible);

public static class BurstDetectionSettingsValidator
{
    public const string ThresholdError = "Burst-detection thresholds must be at least 1.";
    public const string WindowError = "Burst-detection window seconds must be at least 1.";
    public const string CooldownError = "Burst-detection cooldown seconds must not be negative.";

    public static bool TryValidate(BurstDetectionSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.AuthFailureThreshold < 1)
        {
            error = ThresholdError;
            return false;
        }

        if (settings.AuthFailureWindowSeconds < 1)
        {
            error = WindowError;
            return false;
        }

        if (settings.AuthFailureCooldownSeconds < 0)
        {
            error = CooldownError;
            return false;
        }

        if (settings.Id != BurstDetectionSettings.FixedId)
        {
            error = "Burst-detection settings row has an invalid id.";
            return false;
        }

        if (settings.RowVersion < 0)
        {
            error = "Burst-detection settings row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= BurstDetectionSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..BurstDetectionSettings.MaxUpdatedByLength];
    }
}

public interface IBurstDetectionSettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<BurstDetectionSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<BurstDetectionSettingsCreateResult> TryCreateAsync(
        BurstDetectionSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<BurstDetectionSettingsSaveResult> UpdateAsync(
        BurstDetectionSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record BurstDetectionSettingsCreateResult(bool Created, BurstDetectionSettings Settings);

public enum BurstDetectionSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record BurstDetectionSettingsSaveResult(
    BurstDetectionSettingsSaveStatus Status,
    BurstDetectionSettings? Settings)
{
    public bool Succeeded => Status == BurstDetectionSettingsSaveStatus.Saved;

    public static BurstDetectionSettingsSaveResult Saved(BurstDetectionSettings settings) =>
        new(BurstDetectionSettingsSaveStatus.Saved, settings);

    public static BurstDetectionSettingsSaveResult Conflict(BurstDetectionSettings? current) =>
        new(BurstDetectionSettingsSaveStatus.Conflict, current);
}
