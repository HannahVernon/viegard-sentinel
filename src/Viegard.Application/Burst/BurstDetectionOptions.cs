namespace Viegard.Application.Burst;

/// <summary>
/// Code-level fallback defaults for the rate-based burst detector.  These are
/// used only until the durable <see cref="BurstDetectionSettings"/> row is
/// seeded; the Admin UI and read-only API always reflect the seeded values.
/// </summary>
public sealed class BurstDetectionOptions
{
    public const string SectionName = "Viegard:BurstDetection";

    /// <summary>Master switch.  When false, no burst signals are evaluated.</summary>
    public bool GlobalEnabled { get; set; } = true;

    /// <summary>Whether the repeated admin auth-failure signal is evaluated.</summary>
    public bool AuthFailureEnabled { get; set; } = true;

    /// <summary>Number of matching auth failures from one source within the window that fires a proposal.</summary>
    public int AuthFailureThreshold { get; set; } = 5;

    /// <summary>Sliding-window length, in seconds, over which auth failures are counted.</summary>
    public int AuthFailureWindowSeconds { get; set; } = 300;

    /// <summary>Minimum seconds between successive proposals for the same source, to avoid duplicate reviews.</summary>
    public int AuthFailureCooldownSeconds { get; set; } = 3600;

    /// <summary>
    /// When false (the default), a fired proposal is review-only and never eligible
    /// for an unattended action, keeping the burst detector propose-only.
    /// </summary>
    public bool AuthFailureActionEligible { get; set; }
}

/// <summary>Startup validation for burst-detection configuration.</summary>
public sealed class BurstDetectionOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<BurstDetectionOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, BurstDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.AuthFailureThreshold < 1)
        {
            failures.Add($"Burst detection {nameof(options.AuthFailureThreshold)} must be at least 1.");
        }

        if (options.AuthFailureWindowSeconds < 1)
        {
            failures.Add($"Burst detection {nameof(options.AuthFailureWindowSeconds)} must be at least 1.");
        }

        if (options.AuthFailureCooldownSeconds < 0)
        {
            failures.Add($"Burst detection {nameof(options.AuthFailureCooldownSeconds)} must not be negative.");
        }

        return failures.Count > 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail(failures)
            : Microsoft.Extensions.Options.ValidateOptionsResult.Success;
    }
}
