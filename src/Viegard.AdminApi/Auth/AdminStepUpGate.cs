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
        return session.StepUpAt.Value.Add(options.StepUpValidity) >= DateTimeOffset.UtcNow;
    }
}
