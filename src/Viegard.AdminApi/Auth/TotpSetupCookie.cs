using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Viegard.AdminApi.Auth;

/// <summary>
/// Holds the pending TOTP enrolment secret server-side (data-protected, HttpOnly cookie) so the enrolment
/// endpoints never trust a secret posted by the form.
/// </summary>
public sealed class TotpSetupCookie(IDataProtectionProvider dataProtectionProvider, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Viegard.Admin.TotpSetup.v1");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Returns the pending secret for the user, minting and storing a new one when none is valid.
    /// </summary>
    public string GetOrCreate(HttpContext context, Guid userId, Func<string> generateSecret)
    {
        if (TryRead(context, userId, out var existing))
        {
            return existing;
        }

        var secret = generateSecret();
        var ticket = new TotpSetupTicket(userId, secret, _time.GetUtcNow().Add(Lifetime));
        context.Response.Cookies.Append(
            AdminCookieNames.TotpSetup,
            _protector.Protect(JsonSerializer.Serialize(ticket, JsonOptions)),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                MaxAge = Lifetime,
                Path = "/",
            });
        return secret;
    }

    public bool TryRead(HttpContext context, Guid userId, out string secret)
    {
        secret = string.Empty;
        if (!context.Request.Cookies.TryGetValue(AdminCookieNames.TotpSetup, out var protectedValue))
        {
            return false;
        }

        try
        {
            var ticket = JsonSerializer.Deserialize<TotpSetupTicket>(_protector.Unprotect(protectedValue), JsonOptions);
            if (ticket is null
                || ticket.UserId != userId
                || ticket.ExpiresAt <= _time.GetUtcNow()
                || string.IsNullOrWhiteSpace(ticket.Secret))
            {
                return false;
            }

            secret = ticket.Secret;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return false;
        }
    }

    public void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(AdminCookieNames.TotpSetup, new CookieOptions { Path = "/" });

    private sealed record TotpSetupTicket(Guid UserId, string Secret, DateTimeOffset ExpiresAt);
}
