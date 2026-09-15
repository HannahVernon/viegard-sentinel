using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Viegard.AdminApi.Auth;

public sealed class WebAuthnStateCookie(IDataProtectionProvider dataProtectionProvider, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _registrationProtector = dataProtectionProvider.CreateProtector("Viegard.Admin.WebAuthn.Registration.v1");
    private readonly IDataProtector _assertionProtector = dataProtectionProvider.CreateProtector("Viegard.Admin.WebAuthn.Assertion.v1");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public void WriteRegistration(HttpContext context, string serverState) =>
        Write(context, AdminCookieNames.WebAuthnRegistrationState, _registrationProtector, serverState);

    public void WriteAssertion(HttpContext context, string serverState) =>
        Write(context, AdminCookieNames.WebAuthnAssertionState, _assertionProtector, serverState);

    public bool TryReadRegistration(HttpContext context, out string serverState) =>
        TryRead(context, AdminCookieNames.WebAuthnRegistrationState, _registrationProtector, out serverState);

    public bool TryReadAssertion(HttpContext context, out string serverState) =>
        TryRead(context, AdminCookieNames.WebAuthnAssertionState, _assertionProtector, out serverState);

    public void ClearRegistration(HttpContext context) =>
        context.Response.Cookies.Delete(AdminCookieNames.WebAuthnRegistrationState, new CookieOptions { Path = "/auth/webauthn" });

    public void ClearAssertion(HttpContext context) =>
        context.Response.Cookies.Delete(AdminCookieNames.WebAuthnAssertionState, new CookieOptions { Path = "/auth/webauthn" });

    private void Write(HttpContext context, string cookieName, IDataProtector protector, string serverState)
    {
        var ticket = new WebAuthnStateTicket(serverState, _time.GetUtcNow().AddMinutes(5));
        var protectedValue = protector.Protect(JsonSerializer.Serialize(ticket, JsonOptions));
        context.Response.Cookies.Append(cookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = "/auth/webauthn",
        });
    }

    private bool TryRead(HttpContext context, string cookieName, IDataProtector protector, out string serverState)
    {
        serverState = string.Empty;
        if (!context.Request.Cookies.TryGetValue(cookieName, out var protectedValue))
        {
            return false;
        }

        try
        {
            var json = protector.Unprotect(protectedValue);
            var ticket = JsonSerializer.Deserialize<WebAuthnStateTicket>(json, JsonOptions);
            if (ticket is null || ticket.ExpiresAt <= _time.GetUtcNow())
            {
                return false;
            }

            serverState = ticket.ServerState;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private sealed record WebAuthnStateTicket(string ServerState, DateTimeOffset ExpiresAt);
}
