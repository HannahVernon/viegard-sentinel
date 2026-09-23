namespace Viegard.Application.Auth;

/// <summary>
/// Durable, admin-owned configuration for session-security tunables.  A single
/// fixed row (<see cref="FixedId"/>) carries the step-up validity window and the
/// resume-stash lifetime, mirroring the incident-coalescing settings pattern.
/// </summary>
public sealed record SessionSecuritySettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const string SystemSeedActor = "system:session-security-settings-seed";

    public int Id { get; init; } = FixedId;

    public int StepUpValiditySeconds { get; init; } = 300;

    public int ResumeStashTtlSeconds { get; init; } = 600;

    public int RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public SessionSecurityValues ToValues() => new(
        StepUpValiditySeconds,
        ResumeStashTtlSeconds);

    public static SessionSecuritySettings FromOptions(SessionSecurityOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new SessionSecuritySettings
        {
            Id = FixedId,
            StepUpValiditySeconds = options.StepUpValiditySeconds,
            ResumeStashTtlSeconds = options.ResumeStashTtlSeconds,
            RowVersion = 1,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public sealed record SessionSecurityValues(
    int StepUpValiditySeconds,
    int ResumeStashTtlSeconds)
{
    public static SessionSecurityValues FromOptions(SessionSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SessionSecurityValues(
            options.StepUpValiditySeconds,
            options.ResumeStashTtlSeconds);
    }

    public TimeSpan StepUpValidity => TimeSpan.FromSeconds(StepUpValiditySeconds);

    public TimeSpan ResumeStashTtl => TimeSpan.FromSeconds(ResumeStashTtlSeconds);
}

public static class SessionSecuritySettingsValidator
{
    public const string StepUpValidityError = "Session security step-up validity seconds must be at least 1.";
    public const string ResumeStashTtlError = "Session security resume-stash TTL seconds must be at least 1.";

    public static bool TryValidate(SessionSecuritySettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.StepUpValiditySeconds < 1)
        {
            error = StepUpValidityError;
            return false;
        }

        if (settings.ResumeStashTtlSeconds < 1)
        {
            error = ResumeStashTtlError;
            return false;
        }

        if (settings.Id != SessionSecuritySettings.FixedId)
        {
            error = "Session security settings row has an invalid id.";
            return false;
        }

        if (settings.RowVersion < 0)
        {
            error = "Session security settings row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= SessionSecuritySettings.MaxUpdatedByLength
            ? normalized
            : normalized[..SessionSecuritySettings.MaxUpdatedByLength];
    }
}

public interface ISessionSecuritySettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<SessionSecuritySettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<SessionSecuritySettingsCreateResult> TryCreateAsync(
        SessionSecuritySettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<SessionSecuritySettingsSaveResult> UpdateAsync(
        SessionSecuritySettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record SessionSecuritySettingsCreateResult(bool Created, SessionSecuritySettings Settings);

public enum SessionSecuritySettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record SessionSecuritySettingsSaveResult(
    SessionSecuritySettingsSaveStatus Status,
    SessionSecuritySettings? Settings)
{
    public bool Succeeded => Status == SessionSecuritySettingsSaveStatus.Saved;

    public static SessionSecuritySettingsSaveResult Saved(SessionSecuritySettings settings) =>
        new(SessionSecuritySettingsSaveStatus.Saved, settings);

    public static SessionSecuritySettingsSaveResult Conflict(SessionSecuritySettings? current) =>
        new(SessionSecuritySettingsSaveStatus.Conflict, current);
}
