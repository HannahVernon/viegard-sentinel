using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
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
            CapturePendingAction(context);
            return false;
        }

        var sessionSecurityOptions = context.RequestServices.GetRequiredService<IOptions<SessionSecurityOptions>>().Value;
        var sessionSecuritySource = context.RequestServices.GetRequiredService<SessionSecuritySettingsSource>();
        var stepUpValidity = sessionSecuritySource.CurrentValues(sessionSecurityOptions).StepUpValidity;
        var now = DateTimeOffset.UtcNow;
        if (session.StepUpAt.Value.Add(stepUpValidity) < now)
        {
            CapturePendingAction(context);
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

    // Capture the current gated POST so it can be replayed after the operator
    // completes step-up (D-0053).  Reads the already-buffered form (every gated
    // POST reads it before calling the gate), so no synchronous body read is
    // triggered here.  A no-op when there is no buffered form.
    private static void CapturePendingAction(HttpContext context)
    {
        if (context.Features.Get<IFormFeature>()?.Form is { } form)
        {
            StepUpResume.Capture(context, form);
        }
    }
}
