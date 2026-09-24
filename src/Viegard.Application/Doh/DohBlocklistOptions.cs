using Microsoft.Extensions.Options;

namespace Viegard.Application.Doh;

/// <summary>Code-level fallback defaults for the DoH blocklist feature, used only
/// until the durable <see cref="DohBlocklistSettings"/> row is seeded.</summary>
public sealed class DohBlocklistOptions
{
    public const string SectionName = "Viegard:DohBlocklist";

    public bool Enabled { get; set; } = true;

    public string PrimaryFeedUrl { get; set; } = DohBlocklistSettings.DefaultPrimaryFeedUrl;

    public string SecondaryFeedUrl { get; set; } = string.Empty;

    public string AddressListName { get; set; } = DohBlocklistSettings.DefaultAddressListName;

    public int FetchIntervalSeconds { get; set; } = DohBlocklistSettings.DefaultFetchIntervalSeconds;

    public bool ProbeEnabled { get; set; } = true;

    public string ProbeCanaryFqdn { get; set; } = DohBlocklistSettings.DefaultCanaryFqdn;

    public string ProbeExpectedToken { get; set; } = string.Empty;

    public string ProbeEndpointPath { get; set; } = DohBlocklistSettings.DefaultEndpointPath;

    public int ProbeTimeoutSeconds { get; set; } = DohBlocklistSettings.DefaultProbeTimeoutSeconds;

    public int ProbeConcurrency { get; set; } = DohBlocklistSettings.DefaultProbeConcurrency;

    public int ProbeIntervalSeconds { get; set; } = DohBlocklistSettings.DefaultProbeIntervalSeconds;

    public bool ApplyToRouters { get; set; }
}

public sealed class DohBlocklistOptionsValidator : IValidateOptions<DohBlocklistOptions>
{
    public ValidateOptionsResult Validate(string? name, DohBlocklistOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var candidate = DohBlocklistSettings.FromOptions(options, DateTimeOffset.UtcNow);
        return DohBlocklistSettingsValidator.TryValidate(candidate, out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}
