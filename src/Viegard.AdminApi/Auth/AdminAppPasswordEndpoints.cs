using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Viegard.Application.Auth;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Events;
using Microsoft.Extensions.Options;

namespace Viegard.AdminApi.Auth;

/// <summary>
/// Step-up-gated management endpoints for read-only app passwords (D-0040).
/// Creation writes the plaintext token to a one-time cookie consumed by the
/// account page; it is never persisted or logged.
/// </summary>
public static class AdminAppPasswordEndpoints
{
    private const string ReturnPath = "/account#app-passwords";
    public const int MaxNameLength = 128;

    public static void MapAdminAppPasswordEndpoints(this WebApplication app)
    {
        app.MapPost("/account/app-passwords/create", CreateAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/account/app-passwords/revoke", RevokeAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }

    internal static async Task<IResult> CreateAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAppPasswordStore appPasswords,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        IOptions<AdminAuthOptions> authOptions,
        NewAppPasswordCookie newAppPasswordCookie,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireStepUpAsync(
            context,
            users,
            sessions,
            authAuditor,
            "creating app passwords").ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        var name = form["name"].ToString().Trim();
        if (name.Length is 0 or > MaxNameLength)
        {
            return Redirect(error: $"App password name is required and must be at most {MaxNameLength} characters.");
        }

        var now = DateTimeOffset.UtcNow;
        var generated = AppPasswordTokenFormat.Generate();
        var appPassword = new AppPassword
        {
            Id = ViegardId.New(),
            UserId = gate.User!.Id,
            Name = name,
            LookupKey = generated.LookupKey,
            SecretHash = generated.SecretHash,
            CreatedAt = now,
            ExpiresAt = now + authOptions.Value.AppPasswordLifetime,
        };
        await appPasswords.CreateAsync(appPassword, context.RequestAborted).ConfigureAwait(false);
        await configAuditor.RecordAppPasswordCreatedAsync(
            gate.User.Username,
            appPassword.Id,
            name,
            appPassword.ExpiresAt.Value,
            context.RequestAborted).ConfigureAwait(false);
        newAppPasswordCookie.Write(context, generated.Token);
        return Redirect(status: $"App password '{name}' created.  Copy it now: it is shown once and never again.");
    }

    internal static async Task<IResult> RevokeAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAppPasswordStore appPasswords,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireStepUpAsync(
            context,
            users,
            sessions,
            authAuditor,
            "revoking app passwords").ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!Guid.TryParse(form["id"].ToString(), out var id) || id == Guid.Empty)
        {
            return Redirect(error: "App password id was not valid.  Reload the page and try again.");
        }

        var revoked = await appPasswords.RevokeAsync(id, gate.User!.Id, DateTimeOffset.UtcNow, context.RequestAborted)
            .ConfigureAwait(false);
        if (!revoked)
        {
            return Redirect(error: "App password was not found or is already revoked.");
        }

        await configAuditor.RecordAppPasswordRevokedAsync(
            gate.User.Username,
            id,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(status: "App password revoked.  Requests using it stop authenticating immediately.");
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        return await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async ValueTask<GateResult> RequireStepUpAsync(
        HttpContext context,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        string actionLabel)
    {
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return new GateResult(null, Results.Redirect("/login"));
        }

        if (await AdminStepUpGate.HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            return new GateResult(user, null);
        }

        await authAuditor.RecordAsync(
            AdminAuthEventKind.StepUpFailed,
            user.Username,
            context,
            enqueueForCorrelation: true,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
        return new GateResult(user, Redirect(error: $"Step-up verification is required before {actionLabel}."));
    }

    private static async ValueTask<AdminUser?> GetCurrentUserAsync(HttpContext context, IAdminUserStore users)
    {
        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out var userId)
            ? await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false)
            : null;
    }

    private static IResult Redirect(string? status = null, string? error = null) =>
        Results.Redirect(ReturnPath).WithFlash(status: status, error: error);

    private sealed record GateResult(AdminUser? User, IResult? Failure);
}
