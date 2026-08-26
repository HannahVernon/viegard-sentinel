using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Viegard.AdminApi.Auth;

public sealed class RecoveryCodesCookie(IDataProtectionProvider dataProtectionProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Viegard.Admin.RecoveryCodes.v1");

    public void Write(HttpContext context, IReadOnlyList<string> codes)
    {
        var protectedValue = _protector.Protect(JsonSerializer.Serialize(codes, JsonOptions));
        context.Response.Cookies.Append(AdminCookieNames.RecoveryCodes, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = "/account",
        });
    }

    public IReadOnlyList<string> ReadAndClear(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(AdminCookieNames.RecoveryCodes, out var protectedValue))
        {
            return [];
        }

        context.Response.Cookies.Delete(AdminCookieNames.RecoveryCodes, new CookieOptions { Path = "/account" });
        try
        {
            var json = _protector.Unprotect(protectedValue);
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return [];
        }
    }
}
