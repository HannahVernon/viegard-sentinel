using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Viegard.Application.Auth;
using Viegard.Application.Logging;
using Viegard.Application.Stores;

namespace Viegard.AdminApi.Auth;

public static class AppPasswordDefaults
{
    public const string SchemeName = "AppPassword";

    /// <summary>Claim marking a principal as token-authenticated; such principals can never satisfy step-up.</summary>
    public const string AuthMethodClaim = "viegard.auth_method";

    public const string AuthMethodValue = "app-password";

    public const string TokenIdClaim = "viegard.app_password_id";

    /// <summary>Authorization policy for endpoints that read state and accept either a cookie session or an app password.</summary>
    public const string ReadOnlyApiPolicy = "read-only-api";
}

/// <summary>
/// Authenticates <c>Authorization: Bearer viegard_ro_...</c> read-only app
/// passwords.  Fail-closed by construction: the scheme is only consulted by
/// endpoints that explicitly opt into the read-only API policy, the default
/// policy everywhere else remains cookie-only, and the resulting principal
/// carries no session claim so it can never pass the step-up gate.
/// </summary>
public sealed class AppPasswordAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IAppPasswordStore appPasswords,
    IAdminUserStore users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <summary>Last-used stamps are written at most this often per token.</summary>
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromSeconds(60);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!header.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[bearerPrefix.Length..].Trim();
        if (!token.StartsWith(AppPasswordTokenFormat.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        if (!AppPasswordTokenFormat.TryParseLookupKey(token, out var lookupKey))
        {
            return Fail("malformed token");
        }

        var appPassword = await appPasswords.GetByLookupKeyAsync(lookupKey, Context.RequestAborted).ConfigureAwait(false);
        if (appPassword is null || !AppPasswordTokenFormat.Matches(token, appPassword.SecretHash))
        {
            return Fail("unknown or mismatched token");
        }

        var now = DateTimeOffset.UtcNow;
        if (!appPassword.IsUsable(now))
        {
            return Fail(appPassword.RevokedAt is not null ? "revoked token" : "expired token");
        }

        var user = await users.GetByIdAsync(appPassword.UserId, Context.RequestAborted).ConfigureAwait(false);
        if (user is null || user.LockedUntil > now || user.MustChangePassword || !user.TotpEnrolled)
        {
            // Locked, must-change-password, or un-enrolled owners are in a
            // remediation state; their tokens stop authenticating until the
            // account is healthy again.
            return Fail("owner unavailable");
        }

        if (appPassword.LastUsedAt is null || now - appPassword.LastUsedAt >= LastUsedWriteInterval)
        {
            await appPasswords.UpdateLastUsedAsync(appPassword.Id, now, CancellationToken.None).ConfigureAwait(false);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(AppPasswordDefaults.TokenIdClaim, appPassword.Id.ToString()),
            new Claim(AppPasswordDefaults.AuthMethodClaim, AppPasswordDefaults.AuthMethodValue),
        ], Scheme.Name);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Fail(string reason)
    {
        Logger.LogWarning(
            "Rejected app-password request from {RemoteAddress}: {Reason}.",
            LogSanitizer.Sanitize(Context.Connection.RemoteIpAddress?.ToString()),
            reason);
        return AuthenticateResult.Fail($"App password rejected: {reason}.");
    }
}
