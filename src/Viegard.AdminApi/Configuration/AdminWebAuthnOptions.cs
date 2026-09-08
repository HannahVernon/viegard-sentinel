using Microsoft.Extensions.Options;
using Viegard.AdminApi.Auth;

namespace Viegard.AdminApi.Configuration;

public sealed class AdminWebAuthnOptions
{
    public const string SectionName = "Viegard:Admin:WebAuthn";

    public string? RelyingPartyId { get; set; }

    public string[] Origins { get; set; } = [];
}

public sealed class AdminWebAuthnOptionsValidator : IValidateOptions<AdminWebAuthnOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminWebAuthnOptions options)
    {
        var failures = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.RelyingPartyId)
            && options.RelyingPartyId.Contains("://", StringComparison.Ordinal))
        {
            failures.Add("Admin WebAuthn RelyingPartyId must be a host name, not a URL.");
        }

        foreach (var origin in options.Origins)
        {
            if (!WebAuthnConfiguration.TryNormalizeOrigin(origin, out _))
            {
                failures.Add($"Admin WebAuthn origin '{origin}' must be an absolute http or https origin without a path.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
