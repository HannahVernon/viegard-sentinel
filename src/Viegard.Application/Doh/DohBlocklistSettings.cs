namespace Viegard.Application.Doh;

/// <summary>
/// Durable, admin-owned configuration for the DNS-over-HTTPS (DoH) server
/// blocklist feature.  A single fixed row (<see cref="FixedId"/>) carries the
/// master switch, the curated feed URLs, the canary-probe settings, and the
/// router apply toggle.  Confirmed DoH server IPs are reconciled into the
/// pre-existing <c>dns_over_https_servers</c> address list on every managed
/// MikroTik router.  Mirrors the burst-detection and JetPack settings patterns.
/// </summary>
public sealed record DohBlocklistSettings
{
    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const int MaxFeedUrlLength = 2048;
    public const int MaxAddressListNameLength = 128;
    public const int MaxCanaryFqdnLength = 253;
    public const int MaxCanaryTokenLength = 256;
    public const int MaxEndpointPathLength = 256;

    public const string DefaultPrimaryFeedUrl =
        "https://raw.githubusercontent.com/dibdot/DoH-IP-blocklists/master/doh-ipv4.txt";
    public const string DefaultAddressListName = "dns_over_https_servers";
    public const string DefaultCanaryFqdn = "_doh_canary.example.com";
    public const string DefaultEndpointPath = "/dns-query";
    public const int DefaultFetchIntervalSeconds = 3600;
    public const int DefaultProbeIntervalSeconds = 21600;
    public const int DefaultProbeTimeoutSeconds = 5;
    public const int DefaultProbeConcurrency = 8;
    public const string SystemSeedActor = "system:doh-blocklist-settings-seed";

    public int Id { get; init; } = FixedId;

    /// <summary>Master switch.  When false, no feed fetch, probe, or router reconciliation runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Primary curated DoH IPv4 feed (dibdot).  Required.</summary>
    public string PrimaryFeedUrl { get; init; } = DefaultPrimaryFeedUrl;

    /// <summary>Optional secondary curated DoH IPv4 feed (for example jpgpi250).  Empty disables it.</summary>
    public string SecondaryFeedUrl { get; init; } = string.Empty;

    /// <summary>Target MikroTik address list; owned in full by Viegard.</summary>
    public string AddressListName { get; init; } = DefaultAddressListName;

    /// <summary>How often the curated feeds are fetched and merged.</summary>
    public int FetchIntervalSeconds { get; init; } = DefaultFetchIntervalSeconds;

    /// <summary>When true, each candidate IP is confirmed with a canary DoH probe.</summary>
    public bool ProbeEnabled { get; init; } = true;

    /// <summary>Canary FQDN queried over DoH to confirm a reachable, compliant resolver.</summary>
    public string ProbeCanaryFqdn { get; init; } = DefaultCanaryFqdn;

    /// <summary>Expected token published in the canary TXT record.  Blank until seeded by the operator.</summary>
    public string ProbeExpectedToken { get; init; } = string.Empty;

    /// <summary>DoH request path used when probing by IP.</summary>
    public string ProbeEndpointPath { get; init; } = DefaultEndpointPath;

    /// <summary>Per-request probe timeout, in seconds.</summary>
    public int ProbeTimeoutSeconds { get; init; } = DefaultProbeTimeoutSeconds;

    /// <summary>Maximum concurrent probes per cycle.</summary>
    public int ProbeConcurrency { get; init; } = DefaultProbeConcurrency;

    /// <summary>How often confirmed candidates are re-probed.</summary>
    public int ProbeIntervalSeconds { get; init; } = DefaultProbeIntervalSeconds;

    /// <summary>
    /// When false (the default), reconciliation is propose-only: it logs the add/remove
    /// set without changing any router.  When true (and global dry-run is off), confirmed
    /// candidates are pushed to the routers.
    /// </summary>
    public bool ApplyToRouters { get; init; }

    public int RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public DohBlocklistValues ToValues() => new(
        Enabled,
        PrimaryFeedUrl,
        SecondaryFeedUrl,
        AddressListName,
        FetchIntervalSeconds,
        ProbeEnabled,
        ProbeCanaryFqdn,
        ProbeExpectedToken,
        ProbeEndpointPath,
        ProbeTimeoutSeconds,
        ProbeConcurrency,
        ProbeIntervalSeconds,
        ApplyToRouters);

    public static DohBlocklistSettings FromOptions(DohBlocklistOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new DohBlocklistSettings
        {
            Id = FixedId,
            Enabled = options.Enabled,
            PrimaryFeedUrl = options.PrimaryFeedUrl,
            SecondaryFeedUrl = options.SecondaryFeedUrl,
            AddressListName = options.AddressListName,
            FetchIntervalSeconds = options.FetchIntervalSeconds,
            ProbeEnabled = options.ProbeEnabled,
            ProbeCanaryFqdn = options.ProbeCanaryFqdn,
            ProbeExpectedToken = options.ProbeExpectedToken,
            ProbeEndpointPath = options.ProbeEndpointPath,
            ProbeTimeoutSeconds = options.ProbeTimeoutSeconds,
            ProbeConcurrency = options.ProbeConcurrency,
            ProbeIntervalSeconds = options.ProbeIntervalSeconds,
            ApplyToRouters = options.ApplyToRouters,
            RowVersion = 1,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }

    public TimeSpan FetchInterval => TimeSpan.FromSeconds(FetchIntervalSeconds);

    public TimeSpan ProbeInterval => TimeSpan.FromSeconds(ProbeIntervalSeconds);

    public TimeSpan ProbeTimeout => TimeSpan.FromSeconds(ProbeTimeoutSeconds);
}

public sealed record DohBlocklistValues(
    bool Enabled,
    string PrimaryFeedUrl,
    string SecondaryFeedUrl,
    string AddressListName,
    int FetchIntervalSeconds,
    bool ProbeEnabled,
    string ProbeCanaryFqdn,
    string ProbeExpectedToken,
    string ProbeEndpointPath,
    int ProbeTimeoutSeconds,
    int ProbeConcurrency,
    int ProbeIntervalSeconds,
    bool ApplyToRouters)
{
    public static DohBlocklistValues FromOptions(DohBlocklistOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DohBlocklistValues(
            options.Enabled,
            options.PrimaryFeedUrl,
            options.SecondaryFeedUrl,
            options.AddressListName,
            options.FetchIntervalSeconds,
            options.ProbeEnabled,
            options.ProbeCanaryFqdn,
            options.ProbeExpectedToken,
            options.ProbeEndpointPath,
            options.ProbeTimeoutSeconds,
            options.ProbeConcurrency,
            options.ProbeIntervalSeconds,
            options.ApplyToRouters);
    }
}

public static class DohBlocklistSettingsValidator
{
    public const string PrimaryFeedUrlError = "DoH primary feed URL must be an absolute http or https URL.";
    public const string SecondaryFeedUrlError = "DoH secondary feed URL must be empty or an absolute http or https URL.";
    public const string AddressListNameError = "DoH address-list name is required, must not exceed 128 characters, and must not contain control characters.";
    public const string FetchIntervalError = "DoH fetch interval seconds must be at least 60 and no more than 604800 (7 days).";
    public const string ProbeIntervalError = "DoH probe interval seconds must be at least 60 and no more than 604800 (7 days).";
    public const string ProbeTimeoutError = "DoH probe timeout seconds must be between 1 and 60.";
    public const string ProbeConcurrencyError = "DoH probe concurrency must be between 1 and 64.";
    public const string CanaryFqdnError = "DoH canary FQDN is required when probing is enabled and must not exceed 253 characters or contain control characters.";
    public const string EndpointPathError = "DoH probe endpoint path must start with '/' and must not exceed 256 characters or contain control characters.";
    public const string CanaryTokenError = "DoH canary token must not exceed 256 characters or contain control characters.";

    public static bool TryValidate(DohBlocklistSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryNormalizeFeedUrl(settings.PrimaryFeedUrl, required: true, out _, out _))
        {
            error = PrimaryFeedUrlError;
            return false;
        }

        if (!TryNormalizeFeedUrl(settings.SecondaryFeedUrl, required: false, out _, out _))
        {
            error = SecondaryFeedUrlError;
            return false;
        }

        if (!TryNormalizeAddressListName(settings.AddressListName, out _))
        {
            error = AddressListNameError;
            return false;
        }

        if (settings.FetchIntervalSeconds is < 60 or > 604800)
        {
            error = FetchIntervalError;
            return false;
        }

        if (settings.ProbeIntervalSeconds is < 60 or > 604800)
        {
            error = ProbeIntervalError;
            return false;
        }

        if (settings.ProbeTimeoutSeconds is < 1 or > 60)
        {
            error = ProbeTimeoutError;
            return false;
        }

        if (settings.ProbeConcurrency is < 1 or > 64)
        {
            error = ProbeConcurrencyError;
            return false;
        }

        if (settings.ProbeEnabled && !TryNormalizeCanaryFqdn(settings.ProbeCanaryFqdn, out _))
        {
            error = CanaryFqdnError;
            return false;
        }

        if (!TryNormalizeEndpointPath(settings.ProbeEndpointPath, out _))
        {
            error = EndpointPathError;
            return false;
        }

        if (settings.ProbeExpectedToken.Length > DohBlocklistSettings.MaxCanaryTokenLength
            || settings.ProbeExpectedToken.Any(char.IsControl))
        {
            error = CanaryTokenError;
            return false;
        }

        if (settings.Id != DohBlocklistSettings.FixedId)
        {
            error = "DoH blocklist settings row has an invalid id.";
            return false;
        }

        if (settings.RowVersion < 0)
        {
            error = "DoH blocklist settings row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= DohBlocklistSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..DohBlocklistSettings.MaxUpdatedByLength];
    }

    public static bool TryNormalizeFeedUrl(string? value, bool required, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = required ? PrimaryFeedUrlError : string.Empty;
            return !required;
        }

        var candidate = value.Trim();
        if (candidate.Length > DohBlocklistSettings.MaxFeedUrlLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = required ? PrimaryFeedUrlError : SecondaryFeedUrlError;
            return false;
        }

        normalized = uri.AbsoluteUri;
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeAddressListName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length == 0
            || candidate.Length > DohBlocklistSettings.MaxAddressListNameLength
            || candidate.Any(char.IsControl))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    public static bool TryNormalizeCanaryFqdn(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().TrimEnd('.');
        if (candidate.Length == 0
            || candidate.Length > DohBlocklistSettings.MaxCanaryFqdnLength
            || candidate.Any(char.IsControl)
            || candidate.Any(char.IsWhiteSpace)
            || !candidate.Contains('.'))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    public static bool TryNormalizeEndpointPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length == 0
            || candidate.Length > DohBlocklistSettings.MaxEndpointPathLength
            || candidate[0] != '/'
            || candidate.Any(char.IsControl))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}

public interface IDohBlocklistSettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<DohBlocklistSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<DohBlocklistSettingsCreateResult> TryCreateAsync(
        DohBlocklistSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<DohBlocklistSettingsSaveResult> UpdateAsync(
        DohBlocklistSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record DohBlocklistSettingsCreateResult(bool Created, DohBlocklistSettings Settings);

public enum DohBlocklistSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record DohBlocklistSettingsSaveResult(
    DohBlocklistSettingsSaveStatus Status,
    DohBlocklistSettings? Settings)
{
    public bool Succeeded => Status == DohBlocklistSettingsSaveStatus.Saved;

    public static DohBlocklistSettingsSaveResult Saved(DohBlocklistSettings settings) =>
        new(DohBlocklistSettingsSaveStatus.Saved, settings);

    public static DohBlocklistSettingsSaveResult Conflict(DohBlocklistSettings? current) =>
        new(DohBlocklistSettingsSaveStatus.Conflict, current);
}
