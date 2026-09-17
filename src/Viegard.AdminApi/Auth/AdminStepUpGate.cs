using System.Security.Claims;
using Microsoft.Extensions.Options;
using Viegard.Application.Auth;
using Viegard.Application.Stores;

namespace Viegard.AdminApi.Auth;

public static class AdminStepUpGate
{
    public static async ValueTask<bool> HasRecentStepUpAsync(HttpContext context, IAdminSessionStore sessions)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(AdminCookieNames.SessionIdClaim), out var sessionId))
        {
            return false;
        }

        var session = await sessions.GetAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        if (session?.StepUpAt is null)
        {
            return false;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<AdminAuthOptions>>().Value;
        var now = DateTimeOffset.UtcNow;
        if (session.StepUpAt.Value.Add(options.StepUpValidity) < now)
        {
            return false;
        }

        // Sliding freshness (D-0032 amendment, 2026-09-17): each verified
        // action renews the window, so an operator actively working is not
        // re-prompted mid-task.  Idle time beyond the validity still
        // requires a fresh verification.  Page renders do not renew; only
        // gated mutations pass through here.
        await sessions.StampStepUpAsync(sessionId, now, context.RequestAborted).ConfigureAwait(false);
        return true;
    }
}
