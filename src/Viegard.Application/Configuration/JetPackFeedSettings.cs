namespace Viegard.Application.Configuration;

/// <summary>
/// Database-owned JetPack allowlist settings.  Seeded once from environment
/// configuration, then edited on /configuration.
/// </summary>
public sealed record JetPackFeedSettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const int MaxFeedUrlLength = 2048;
    public const int MaxAddressListNameLength = 128;
    public const string DefaultFeedUrl = "https://jetpack.com/ips-v4.json";
    public const string DefaultAddressListName = "jetpack_servers";
    public static readonly TimeSpan DefaultFetchInterval = TimeSpan.FromHours(1);
    public const string SystemSeedActor = "system:jetpack-feed-seed";

    public int Id { get; init; } = FixedId;

    public string FeedUrl { get; init; } = DefaultFeedUrl;

    public TimeSpan FetchInterval { get; init; } = DefaultFetchInterval;

    public bool Enabled { get; init; } = true;

    public string AddressListName { get; init; } = DefaultAddressListName;

    public int Version { get; init; }

    public DateTimeOffset? SeededAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public static JetPackFeedSettings FromOptions(JetPackFeedOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new JetPackFeedSettings
        {
            Id = FixedId,
            FeedUrl = options.FeedUrl,
            FetchInterval = options.FetchInterval,
            Enabled = options.Enabled,
            AddressListName = options.AddressListName,
            Version = 1,
            SeededAt = utc,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public static class JetPackFeedSettingsValidator
{
    public const string FeedUrlError = "JetPack feed URL must be an absolute http or https URL.";
    public const string FetchIntervalError = "JetPack fetch interval must be greater than zero and no more than 7 days.";
    public const string AddressListNameError = "JetPack address-list name is required, must not exceed 128 characters, and must not contain control characters.";

    public static bool TryValidate(JetPackFeedSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryNormalizeFeedUrl(settings.FeedUrl, out _, out error)
            || !TryNormalizeAddressListName(settings.AddressListName, out _, out error))
        {
            return false;
        }

        if (settings.FetchInterval <= TimeSpan.Zero || settings.FetchInterval > TimeSpan.FromDays(7))
        {
            error = FetchIntervalError;
            return false;
        }

        if (settings.Id != JetPackFeedSettings.FixedId)
        {
            error = "JetPack feed settings row has an invalid id.";
            return false;
        }

        if (settings.Version < 0)
        {
            error = "JetPack feed settings version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= JetPackFeedSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..JetPackFeedSettings.MaxUpdatedByLength];
    }

    public static bool TryNormalizeFeedUrl(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = FeedUrlError;
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > JetPackFeedSettings.MaxFeedUrlLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = FeedUrlError;
            return false;
        }

        normalized = uri.AbsoluteUri;
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeAddressListName(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = AddressListNameError;
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length == 0
            || candidate.Length > JetPackFeedSettings.MaxAddressListNameLength
            || candidate.Any(char.IsControl))
        {
            error = AddressListNameError;
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }
}

/// <summary>Persistence port for runtime-owned JetPack feed settings.</summary>
public interface IJetPackFeedSettingsStore
{
    ValueTask<JetPackFeedSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<JetPackFeedSettingsSaveResult> UpsertAsync(
        JetPackFeedSettings settings,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<JetPackFeedSettings?> SeedIfMissingAsync(
        JetPackFeedOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default);
}

public enum JetPackFeedSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record JetPackFeedSettingsSaveResult(
    JetPackFeedSettingsSaveStatus Status,
    JetPackFeedSettings? Settings)
{
    public bool Succeeded => Status == JetPackFeedSettingsSaveStatus.Saved;

    public static JetPackFeedSettingsSaveResult Saved(JetPackFeedSettings settings) =>
        new(JetPackFeedSettingsSaveStatus.Saved, settings);

    public static JetPackFeedSettingsSaveResult Conflict(JetPackFeedSettings? current) =>
        new(JetPackFeedSettingsSaveStatus.Conflict, current);
}
