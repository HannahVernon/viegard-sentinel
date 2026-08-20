using Microsoft.Extensions.Options;
using Viegard.Domain.Classifications;

namespace Viegard.Application.Policy;

/// <summary>Configurable policy thresholds and guardrails for Judgment.</summary>
public sealed class PolicyOptions
{
    public const string SectionName = "Viegard:Policy";

    public double EvidenceScoreBlockThreshold { get; set; } = 3.0;

    public double AiActionConfidence { get; set; } = 0.9;

    public int AiActionMinSeverity { get; set; } = 7;

    public double AiReviewConfidence { get; set; } = 0.7;

    public TimeSpan TempBanDuration { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan RepeatOffenderBanDuration { get; set; } = TimeSpan.FromDays(7);

    public int RepeatOffenderIncidentCount { get; set; } = 3;

    public TimeSpan RepeatOffenderWindow { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan MaxAutoBanDuration { get; set; } = TimeSpan.FromDays(30);

    public int MaxAutoActionsPerHour { get; set; } = 20;

    public int MaxAutoActionsPerDay { get; set; } = 100;

    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    public IList<string> ProtectedCidrs { get; set; } =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "0.0.0.0/8",
        "255.255.255.255/32",
        "::1/128",
        "fe80::/10",
        "fc00::/7",
        "ff00::/8",
    ];

    public IList<string> AllowedSenders { get; set; } = [];

    public IList<string> AllowedDomains { get; set; } = [];
}

/// <summary>Startup validation for policy configuration.</summary>
public sealed class PolicyOptionsValidator : IValidateOptions<PolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, PolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidatePositive(options.EvidenceScoreBlockThreshold, nameof(options.EvidenceScoreBlockThreshold), failures);
        ValidateConfidence(options.AiActionConfidence, nameof(options.AiActionConfidence), failures);
        ValidateConfidence(options.AiReviewConfidence, nameof(options.AiReviewConfidence), failures);

        if (options.AiReviewConfidence > options.AiActionConfidence)
        {
            failures.Add("Policy AiReviewConfidence must be less than or equal to AiActionConfidence.");
        }

        if (options.AiActionMinSeverity is < Classification.MinSeverity or > Classification.MaxSeverity)
        {
            failures.Add(
                $"Policy AiActionMinSeverity must be between {Classification.MinSeverity} and {Classification.MaxSeverity}.");
        }

        ValidatePositive(options.TempBanDuration, nameof(options.TempBanDuration), failures);
        ValidatePositive(options.RepeatOffenderBanDuration, nameof(options.RepeatOffenderBanDuration), failures);
        ValidatePositive(options.RepeatOffenderWindow, nameof(options.RepeatOffenderWindow), failures);
        ValidatePositive(options.MaxAutoBanDuration, nameof(options.MaxAutoBanDuration), failures);
        ValidatePositive(options.RepeatOffenderIncidentCount, nameof(options.RepeatOffenderIncidentCount), failures);
        ValidatePositive(options.MaxAutoActionsPerHour, nameof(options.MaxAutoActionsPerHour), failures);
        ValidatePositive(options.MaxAutoActionsPerDay, nameof(options.MaxAutoActionsPerDay), failures);
        ValidatePositive(options.CircuitBreakerFailureThreshold, nameof(options.CircuitBreakerFailureThreshold), failures);

        if (options.TempBanDuration > options.MaxAutoBanDuration)
        {
            failures.Add("Policy TempBanDuration must be less than or equal to MaxAutoBanDuration.");
        }

        if (options.RepeatOffenderBanDuration > options.MaxAutoBanDuration)
        {
            failures.Add("Policy RepeatOffenderBanDuration must be less than or equal to MaxAutoBanDuration.");
        }

        for (var i = 0; i < options.ProtectedCidrs.Count; i++)
        {
            var cidr = options.ProtectedCidrs[i];
            if (!ProtectedAddressList.TryParseCidr(cidr, out var failure))
            {
                failures.Add($"Policy ProtectedCidrs[{i}] '{cidr}' is invalid: {failure}");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateConfidence(double value, string name, List<string> failures)
    {
        if (value is < 0.0 or > 1.0 || double.IsNaN(value))
        {
            failures.Add($"Policy {name} must be within [0.0, 1.0].");
        }
    }

    private static void ValidatePositive(double value, string name, List<string> failures)
    {
        if (value <= 0.0 || double.IsNaN(value))
        {
            failures.Add($"Policy {name} must be positive.");
        }
    }

    private static void ValidatePositive(TimeSpan value, string name, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"Policy {name} must be positive.");
        }
    }

    private static void ValidatePositive(int value, string name, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"Policy {name} must be positive.");
        }
    }
}
