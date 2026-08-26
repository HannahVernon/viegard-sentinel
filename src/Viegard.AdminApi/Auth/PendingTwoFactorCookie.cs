using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Viegard.AdminApi.Auth;

public sealed class PendingTwoFactorCookie(IDataProtectionProvider dataProtectionProvider, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Viegard.Admin.PendingTwoFactor.v1");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public void Write(HttpContext context, Guid userId)
    {
        var ticket = new PendingTwoFactorTicket(userId, _time.GetUtcNow().AddMinutes(5));
        var protectedValue = _protector.Protect(JsonSerializer.Serialize(ticket, JsonOptions));
        context.Response.Cookies.Append(AdminCookieNames.PendingTwoFactor, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = "/",
        });
    }

    public bool TryRead(HttpContext context, out Guid userId)
    {
        userId = default;
        if (!context.Request.Cookies.TryGetValue(AdminCookieNames.PendingTwoFactor, out var protectedValue))
        {
            return false;
        }

        try
        {
            var json = _protector.Unprotect(protectedValue);
            var ticket = JsonSerializer.Deserialize<PendingTwoFactorTicket>(json, JsonOptions);
            if (ticket is null || ticket.ExpiresAt <= _time.GetUtcNow())
            {
                return false;
            }

            userId = ticket.UserId;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return false;
        }
    }

    public void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(AdminCookieNames.PendingTwoFactor, new CookieOptions { Path = "/" });

    private sealed record PendingTwoFactorTicket(Guid UserId, DateTimeOffset ExpiresAt);
}
