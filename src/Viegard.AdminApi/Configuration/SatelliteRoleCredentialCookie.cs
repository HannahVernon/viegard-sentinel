using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Viegard.AdminApi.Auth;
using Viegard.Application.Configuration;

namespace Viegard.AdminApi.Configuration;

public sealed class SatelliteRoleCredentialCookie(IDataProtectionProvider dataProtectionProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Viegard.Admin.SatelliteRoleCredential.v1");

    public void Write(HttpContext context, SatelliteRoleSecret credential)
    {
        var protectedValue = _protector.Protect(JsonSerializer.Serialize(credential, JsonOptions));
        context.Response.Cookies.Append(AdminCookieNames.SatelliteRoleSecret, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = "/configuration",
        });
    }

    public SatelliteRoleSecret? ReadAndClear(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(AdminCookieNames.SatelliteRoleSecret, out var protectedValue))
        {
            return null;
        }

        context.Response.Cookies.Delete(AdminCookieNames.SatelliteRoleSecret, new CookieOptions { Path = "/configuration" });
        try
        {
            var json = _protector.Unprotect(protectedValue);
            return JsonSerializer.Deserialize<SatelliteRoleSecret>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }
}
