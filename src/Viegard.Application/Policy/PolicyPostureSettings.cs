namespace Viegard.Application.Policy;

/// <summary>
/// Database-owned policy posture flags (D-0038 amendment, 2026-09-17).
/// Seeded once from the environment posture values by the maintenance role,
/// thereafter UI-owned.  DryRun composes the router calls without applying
/// them; ManualApprovalMode routes every would-be action to operator review
/// (and wins over DryRun in the decision overlay); EmergencyStop refuses all
/// action execution.
/// </summary>
public sealed record PolicyPostureSettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const string SystemSeedActor = "system:policy-posture-seed";

    public int Id { get; init; } = FixedId;

    public bool DryRun { get; init; } = true;

    public bool ManualApprovalMode { get; init; } = true;

    public bool EmergencyStop { get; init; }

    public int RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public static PolicyPostureSettings FromOptions(PolicyOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new PolicyPostureSettings
        {
            Id = FixedId,
            DryRun = options.Posture.DryRun,
            ManualApprovalMode = options.Posture.ManualApprovalMode,
            EmergencyStop = options.Posture.EmergencyStop,
            RowVersion = 1,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public static class PolicyPostureSettingsValidator
{
    public static bool TryValidate(PolicyPostureSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Id != PolicyPostureSettings.FixedId)
        {
            error = "Policy posture settings row has an invalid id.";
            return false;
        }

        if (settings.RowVersion < 0)
        {
            error = "Policy posture settings row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= PolicyPostureSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..PolicyPostureSettings.MaxUpdatedByLength];
    }
}

public interface IPolicyPostureSettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<PolicyPostureSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<PolicyPostureSettingsCreateResult> TryCreateAsync(
        PolicyPostureSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<PolicyPostureSettingsSaveResult> UpdateAsync(
        PolicyPostureSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record PolicyPostureSettingsCreateResult(bool Created, PolicyPostureSettings Settings);

public enum PolicyPostureSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record PolicyPostureSettingsSaveResult(
    PolicyPostureSettingsSaveStatus Status,
    PolicyPostureSettings? Settings)
{
    public bool Succeeded => Status == PolicyPostureSettingsSaveStatus.Saved;

    public static PolicyPostureSettingsSaveResult Saved(PolicyPostureSettings settings) =>
        new(PolicyPostureSettingsSaveStatus.Saved, settings);

    public static PolicyPostureSettingsSaveResult Conflict(PolicyPostureSettings? current) =>
        new(PolicyPostureSettingsSaveStatus.Conflict, current);
}
