using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Configuration;

public sealed class HostUpgradeAgentOptions
{
    public const string SectionName = "Viegard:HostUpgradeAgent";
    public const string DefaultScheduledTaskName = "ViegardSatelliteMDaemonAutoUpgrade";
    public const int MinPollIntervalSeconds = 5;
    public const int MaxPollIntervalSeconds = 300;
    public const int MinStuckStateGracePeriodMinutes = 1;
    public const int MaxStuckStateGracePeriodMinutes = 60;

    public string Target { get; set; } = string.Empty;

    public string SatelliteScriptPath { get; set; } = string.Empty;

    public string ClientName { get; set; } = "MDaemon";

    public string ScheduledTaskName { get; set; } = DefaultScheduledTaskName;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan StuckStateGracePeriod { get; set; } = TimeSpan.FromMinutes(10);

    public bool Enabled => !string.IsNullOrWhiteSpace(Target);
}

public sealed class HostUpgradeAgentOptionsValidator : IValidateOptions<HostUpgradeAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, HostUpgradeAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.PollInterval < TimeSpan.FromSeconds(HostUpgradeAgentOptions.MinPollIntervalSeconds)
            || options.PollInterval > TimeSpan.FromSeconds(HostUpgradeAgentOptions.MaxPollIntervalSeconds))
        {
            failures.Add("Host upgrade agent poll interval must be between 5 seconds and 5 minutes.");
        }

        if (options.StuckStateGracePeriod < TimeSpan.FromMinutes(HostUpgradeAgentOptions.MinStuckStateGracePeriodMinutes)
            || options.StuckStateGracePeriod > TimeSpan.FromMinutes(HostUpgradeAgentOptions.MaxStuckStateGracePeriodMinutes))
        {
            failures.Add("Host upgrade agent stuck-state grace period must be between 1 and 60 minutes.");
        }

        if (!options.Enabled)
        {
            return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
        }

        string normalizedTarget;
        try
        {
            normalizedTarget = HostUpgradeCommandPolicy.NormalizeTarget(options.Target);
        }
        catch (ArgumentException ex)
        {
            failures.Add("Host upgrade agent target is invalid.  " + ex.Message);
            normalizedTarget = string.Empty;
        }

        if (string.Equals(normalizedTarget, HostUpgradeCommandPolicy.DefaultTarget, StringComparison.Ordinal))
        {
            failures.Add("Host upgrade agent target must not be 'vm'.  The vm target belongs to the Linux host agent.");
        }

        if (string.IsNullOrWhiteSpace(options.SatelliteScriptPath))
        {
            failures.Add("Host upgrade agent satellite script path is required when Target is configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientName))
        {
            failures.Add("Host upgrade agent client name is required when Target is configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ScheduledTaskName))
        {
            failures.Add("Host upgrade agent scheduled task name is required when Target is configured.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
