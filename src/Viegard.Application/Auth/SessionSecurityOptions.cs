using Microsoft.Extensions.Options;

namespace Viegard.Application.Auth;

/// <summary>
/// Code-level fallback defaults for session-security tunables.  These are used
/// only until the durable <see cref="SessionSecuritySettings"/> row is seeded;
/// the Admin UI and read-only API always reflect the seeded values.
/// </summary>
public sealed class SessionSecurityOptions
{
    public const string SectionName = "Viegard:Admin:SessionSecurity";

    /// <summary>
    /// How long a step-up verification stays valid, in seconds.  Each verified
    /// sensitive action renews the window (sliding freshness, D-0032).  Mirrors
    /// the historical <see cref="AdminAuthOptions.StepUpValidity"/> default.
    /// </summary>
    public int StepUpValiditySeconds { get; set; } = 300;

    /// <summary>
    /// How long a captured pending action waits to be resumed after a failed
    /// step-up gate, in seconds.  The operator must complete verification within
    /// this window for the original action (Approve/Reject/save) to be replayed.
    /// </summary>
    public int ResumeStashTtlSeconds { get; set; } = 600;
}

/// <summary>Startup validation for session-security configuration.</summary>
public sealed class SessionSecurityOptionsValidator : IValidateOptions<SessionSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, SessionSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.StepUpValiditySeconds < 1)
        {
            failures.Add($"Session security {nameof(options.StepUpValiditySeconds)} must be at least 1.");
        }

        if (options.ResumeStashTtlSeconds < 1)
        {
            failures.Add($"Session security {nameof(options.ResumeStashTtlSeconds)} must be at least 1.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
