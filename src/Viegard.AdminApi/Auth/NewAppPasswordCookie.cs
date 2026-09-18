using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Viegard.AdminApi.Auth;

/// <summary>
/// One-time transport for a freshly created app-password token between the
/// create endpoint and the account page, mirroring the recovery-codes
/// pattern: DataProtection-encrypted, HttpOnly, short-lived, deleted on
/// first read.  The plaintext token exists nowhere else.
/// </summary>
public sealed class NewAppPasswordCookie(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Viegard.Admin.NewAppPassword.v1");

    public void Write(HttpContext context, string token)
    {
        var protectedValue = _protector.Protect(token);
        context.Response.Cookies.Append(AdminCookieNames.NewAppPassword, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = "/account",
        });
    }

    public string? ReadAndClear(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(AdminCookieNames.NewAppPassword, out var protectedValue))
        {
            return null;
        }

        context.Response.Cookies.Delete(AdminCookieNames.NewAppPassword, new CookieOptions { Path = "/account" });
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
