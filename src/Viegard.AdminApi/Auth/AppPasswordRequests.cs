using Microsoft.AspNetCore.Authorization;
using Viegard.Application.Auth;

namespace Viegard.AdminApi.Auth;

/// <summary>
/// Request classification helpers for the global authentication gate.  The
/// gate runs before endpoint authorization, where <c>context.User</c> holds
/// only the default cookie scheme's result, so app-password bearers must be
/// recognized and authenticated there explicitly (found live: every token
/// request was cookie-challenged to /login before the handler ever ran).
/// </summary>
public static class AppPasswordRequests
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>True when the request presents a bearer token in the app-password format.</summary>
    public static bool HasAppPasswordBearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && header.AsSpan(BearerPrefix.Length).TrimStart()
                .StartsWith(AppPasswordTokenFormat.Prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// True only when the resolved endpoint is explicitly opted into the
    /// read-only API policy.  Everything else stays cookie-only, so a token
    /// principal can never widen the authenticated surface.
    /// </summary>
    public static bool EndpointAcceptsAppPasswords(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(data => string.Equals(data.Policy, AppPasswordDefaults.ReadOnlyApiPolicy, StringComparison.Ordinal)) == true;
}
