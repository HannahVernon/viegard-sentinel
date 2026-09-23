namespace Viegard.Application.Coalescing;

/// <summary>
/// Durable, admin-owned configuration for incident coalescing.  A single fixed
/// row (<see cref="FixedId"/>) carries the master switch and the window
/// tunables, mirroring the burst-detection settings pattern.
/// </summary>
public sealed record IncidentCoalescingSettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const string SystemSeedActor = "system:incident-coalescing-settings-seed";

    public int Id { get; init; } = FixedId;

    public bool Enabled { get; init; } = true;

    public int SettleWindowSeconds { get; init; } = 10;

    public int MaxCoalesceWindowSeconds { get; init; } = 300;

    public int RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public IncidentCoalescingValues ToValues() => new(
        Enabled,
        SettleWindowSeconds,
        MaxCoalesceWindowSeconds);

    public static IncidentCoalescingSettings FromOptions(IncidentCoalescingOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new IncidentCoalescingSettings
        {
            Id = FixedId,
            Enabled = options.Enabled,
            SettleWindowSeconds = options.SettleWindowSeconds,
            MaxCoalesceWindowSeconds = options.MaxCoalesceWindowSeconds,
            RowVersion = 1,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public sealed record IncidentCoalescingValues(
    bool Enabled,
    int SettleWindowSeconds,
    int MaxCoalesceWindowSeconds)
{
    public static IncidentCoalescingValues FromOptions(IncidentCoalescingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new IncidentCoalescingValues(
            options.Enabled,
            options.SettleWindowSeconds,
            options.MaxCoalesceWindowSeconds);
    }

    public TimeSpan SettleWindow => TimeSpan.FromSeconds(SettleWindowSeconds);

    public TimeSpan MaxCoalesceWindow => TimeSpan.FromSeconds(MaxCoalesceWindowSeconds);
}

public static class IncidentCoalescingSettingsValidator
{
    public const string SettleWindowError = "Incident coalescing settle-window seconds must be at least 1.";
    public const string MaxWindowError = "Incident coalescing max window seconds must be at least the settle-window seconds.";

    public static bool TryValidate(IncidentCoalescingSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.SettleWindowSeconds < 1)
        {
            error = SettleWindowError;
            return false;
        }

        if (settings.MaxCoalesceWindowSeconds < settings.SettleWindowSeconds)
        {
            error = MaxWindowError;
            return false;
        }

        if (settings.Id != IncidentCoalescingSettings.FixedId)
        {
            error = "Incident coalescing settings row has an invalid id.";
            return false;
        }

        if (settings.RowVersion < 0)
        {
            error = "Incident coalescing settings row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= IncidentCoalescingSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..IncidentCoalescingSettings.MaxUpdatedByLength];
    }
}

public interface IIncidentCoalescingSettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<IncidentCoalescingSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<IncidentCoalescingSettingsCreateResult> TryCreateAsync(
        IncidentCoalescingSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<IncidentCoalescingSettingsSaveResult> UpdateAsync(
        IncidentCoalescingSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record IncidentCoalescingSettingsCreateResult(bool Created, IncidentCoalescingSettings Settings);

public enum IncidentCoalescingSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record IncidentCoalescingSettingsSaveResult(
    IncidentCoalescingSettingsSaveStatus Status,
    IncidentCoalescingSettings? Settings)
{
    public bool Succeeded => Status == IncidentCoalescingSettingsSaveStatus.Saved;

    public static IncidentCoalescingSettingsSaveResult Saved(IncidentCoalescingSettings settings) =>
        new(IncidentCoalescingSettingsSaveStatus.Saved, settings);

    public static IncidentCoalescingSettingsSaveResult Conflict(IncidentCoalescingSettings? current) =>
        new(IncidentCoalescingSettingsSaveStatus.Conflict, current);
}
