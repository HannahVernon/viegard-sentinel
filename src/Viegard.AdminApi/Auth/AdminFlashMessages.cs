using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Viegard.AdminApi.Auth;

/// <summary>A one-shot banner message consumed by the next page render.</summary>
public sealed record AdminFlashMessage(string Message, bool IsError);

/// <summary>
/// Server-owned flash messages for post-redirect status and error banners.
/// The message travels in a short-lived, DataProtection-protected cookie
/// instead of the query string, so a crafted URL cannot make the admin UI
/// display attacker-chosen text in its trusted banner styling.  Only code
/// holding the server's DataProtection keys can mint a message.
/// </summary>
public static class AdminFlashMessages
{
    public const string CookieName = "viegard-flash";
    private const string ProtectorPurpose = "Viegard.AdminApi.FlashMessages.v1";
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Wraps an inner result so the flash cookie is written when the result
    /// executes.  Returns the inner result unchanged when no message is set.
    /// Status takes precedence over error, matching the previous
    /// query-string behavior.
    /// </summary>
    public static IResult WithFlash(this IResult inner, string? status = null, string? error = null) =>
        string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(error)
            ? inner
            : new FlashResult(inner, status, error);

    public static void Set(HttpContext context, string? status = null, string? error = null, TimeProvider? timeProvider = null)
    {
        var message = string.IsNullOrWhiteSpace(status) ? error : status;
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var isError = string.IsNullOrWhiteSpace(status);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var payload = JsonSerializer.Serialize(new FlashPayload(message, isError, now), SerializerOptions);
        context.Response.Cookies.Append(
            CookieName,
            CreateProtector(context).Protect(payload),
            BuildCookieOptions(MaxAge));
    }

    /// <summary>
    /// Reads, validates, and deletes the flash cookie.  Returns null for a
    /// missing, tampered, malformed, or expired cookie.
    /// </summary>
    public static AdminFlashMessage? Consume(HttpContext context, TimeProvider? timeProvider = null)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var raw) || string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!context.Response.HasStarted)
        {
            context.Response.Cookies.Delete(CookieName, BuildCookieOptions(maxAge: null));
        }

        try
        {
            var payload = JsonSerializer.Deserialize<FlashPayload>(
                CreateProtector(context).Unprotect(raw),
                SerializerOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Message))
            {
                return null;
            }

            var age = (timeProvider ?? TimeProvider.System).GetUtcNow() - payload.IssuedAt;
            if (age < TimeSpan.Zero || age > MaxAge)
            {
                return null;
            }

            return new AdminFlashMessage(payload.Message, payload.IsError);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IDataProtector CreateProtector(HttpContext context) =>
        context.RequestServices
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(ProtectorPurpose);

    private static CookieOptions BuildCookieOptions(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = maxAge,
        Path = "/",
        IsEssential = true,
    };

    private sealed record FlashPayload(string Message, bool IsError, DateTimeOffset IssuedAt);

    private sealed class FlashResult(IResult inner, string? status, string? error) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            Set(httpContext, status, error);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
