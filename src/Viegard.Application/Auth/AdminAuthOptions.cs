using Microsoft.Extensions.Options;
using Viegard.Application.Net;

namespace Viegard.Application.Auth;

public static class AdminIpBindingModes
{
    public const string Strict = "strict";
    public const string Subnet = "subnet";
    public const string LogOnly = "log-only";

    public static bool IsKnown(string? mode) =>
        string.Equals(mode, Strict, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Subnet, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, LogOnly, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string mode) => mode.ToLowerInvariant();
}

public sealed class AdminAuthOptions
{
    public const string SectionName = "Viegard:Admin:Auth";

    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromDays(14);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(48);

    public string IpBindingMode { get; set; } = AdminIpBindingModes.Strict;

    // Provisional default from D-0032.  Sensitive-operation coverage expands
    // as Phase 8 adds runtime configuration editors.
    public TimeSpan StepUpValidity { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class AdminAuthOptionsValidator : IValidateOptions<AdminAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminAuthOptions options)
    {
        var failures = new List<string>();
        if (options.AbsoluteLifetime <= TimeSpan.Zero)
        {
            failures.Add("Admin Auth: AbsoluteLifetime must be positive.");
        }

        if (options.AbsoluteLifetime > TimeSpan.FromDays(30))
        {
            failures.Add("Admin Auth: AbsoluteLifetime must not exceed 30 days.");
        }

        if (options.IdleTimeout <= TimeSpan.Zero)
        {
            failures.Add("Admin Auth: IdleTimeout must be positive.");
        }

        if (options.StepUpValidity <= TimeSpan.Zero)
        {
            failures.Add("Admin Auth: StepUpValidity must be positive.");
        }

        if (!AdminIpBindingModes.IsKnown(options.IpBindingMode))
        {
            failures.Add("Admin Auth: IpBindingMode must be strict, subnet, or log-only.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class AdminAllowedSourcesOptions
{
    public const string SectionName = "Viegard:Admin";

    public string[] AllowedSources { get; set; } = [];
}

public sealed class AdminAllowedSourcesOptionsValidator : IValidateOptions<AdminAllowedSourcesOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminAllowedSourcesOptions options)
    {
        var failures = new List<string>();
        foreach (var source in options.AllowedSources)
        {
            if (!CidrSet.TryParseEntry(source, out var failure))
            {
                failures.Add($"Admin AllowedSources entry '{source}' is invalid: {failure}");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
