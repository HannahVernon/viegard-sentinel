using Microsoft.AspNetCore.Antiforgery;
using Viegard.AdminApi.Auth;
using Viegard.Application.Stores;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Errors;

public static class AdminErrorEndpoints
{
    private const string ErrorsPath = "/errors";

    public static void MapAdminErrorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/errors/clear", ClearAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }

    internal static async Task<IResult> ClearAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminErrorStore errors,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var user = Guid.TryParse(userIdClaim, out var userId)
            ? await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false)
            : null;
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!await AdminStepUpGate.HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await authAuditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Results.Redirect(ErrorsPath).WithFlash(error: "Step-up verification is required before clearing captured errors.");
        }

        var removed = await errors.ClearAsync(context.RequestAborted).ConfigureAwait(false);
        await configAuditor.RecordAdminErrorsClearedAsync(user.Username, removed, context.RequestAborted).ConfigureAwait(false);
        return Results.Redirect(ErrorsPath).WithFlash(status: $"Cleared {removed} captured errors.");
    }
}
