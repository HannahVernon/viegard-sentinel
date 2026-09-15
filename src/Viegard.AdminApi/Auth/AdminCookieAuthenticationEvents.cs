using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Viegard.Application.Auth;
using Viegard.Application.Logging;
using Viegard.Application.Stores;

namespace Viegard.AdminApi.Auth;

public sealed class AdminCookieAuthenticationEvents(
    IAdminSessionStore sessions,
    IOptions<AdminAuthOptions> options,
    ILogger<AdminCookieAuthenticationEvents> logger) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var userIdClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionIdClaim = context.Principal?.FindFirstValue(AdminCookieNames.SessionIdClaim);
        if (!Guid.TryParse(userIdClaim, out var userId) || !Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            Reject(context);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var session = await sessions.GetAsync(sessionId, context.HttpContext.RequestAborted).ConfigureAwait(false);
        var remoteAddress = context.HttpContext.Connection.RemoteIpAddress;
        var validation = AdminSessionValidator.Validate(session, userId, remoteAddress, now, options.Value.IdleTimeout);
        if (!validation.IsValid)
        {
            if (validation.IpBinding is { Allowed: false } bindingFailure && session is not null && remoteAddress is not null)
            {
                logger.LogWarning(
                    "Rejected admin session {SessionId} for IP binding mismatch.  Original={OriginalIp}; Current={CurrentIp}; Mode={Mode}.",
                    sessionId,
                    LogSanitizer.Sanitize(session.Ip),
                    LogSanitizer.Sanitize(remoteAddress.ToString()),
                    bindingFailure.Mode);
            }

            Reject(context);
            return;
        }

        if (validation.IpBinding is { Matched: false })
        {
            logger.LogWarning(
                "Admin session {SessionId} IP binding mismatch allowed in log-only mode.  Original={OriginalIp}; Current={CurrentIp}.",
                sessionId,
                LogSanitizer.Sanitize(session!.Ip),
                LogSanitizer.Sanitize(remoteAddress!.ToString()));
        }

        if (validation.ShouldRefreshActivity && validation.RefreshedIdleExpiresAt is not null)
        {
            await sessions.UpdateActivityAsync(
                session!.Id,
                now,
                validation.RefreshedIdleExpiresAt.Value,
                context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private static void Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        context.HttpContext.Response.Cookies.Delete(AdminCookieNames.Session, new CookieOptions { Path = "/" });
    }
}
