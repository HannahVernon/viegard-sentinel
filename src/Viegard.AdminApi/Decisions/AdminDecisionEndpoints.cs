using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Viegard.Actions.MikroTik;
using Viegard.AdminApi.Auth;
using Viegard.Application.Actions;
using Viegard.Application.Auth;
using Viegard.Application.Policy;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Decisions;

public static class AdminDecisionEndpoints
{
    private const string DecisionsPath = "/decisions";
    private const string BansPath = "/bans";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAdminDecisionEndpoints(this WebApplication app)
    {
        app.MapPost("/decisions/review", ReviewDecisionAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/decisions/bulk-reject", BulkRejectAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/bans/unban", UnbanAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }

    internal static async Task<IResult> ReviewDecisionAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IDecisionStore decisions,
        DecisionTargetResolver targetResolver,
        IActionStore actions,
        IWorkQueue<ActionWorkItem> actionQueue,
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
            DecisionDetailPath(ReadDecisionIdOrEmpty(form)),
            "reviewing decisions").ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!TryReadDecisionId(form, out var decisionId, out var error))
        {
            return Redirect(DecisionsPath, error: error);
        }

        var path = DecisionDetailPath(decisionId);
        if (!TryReadVerdict(form["verdict"].ToString(), out var reviewOutcome, out error))
        {
            return Redirect(path, error: error);
        }

        var decision = await decisions.GetAsync(decisionId, context.RequestAborted).ConfigureAwait(false);
        if (decision is null)
        {
            return Redirect(path, error: "Decision was not found.  Reload the page and try again.");
        }

        if (decision.Outcome != DecisionOutcome.RequireApproval)
        {
            return Redirect(path, error: "Only require approval decisions can be reviewed.");
        }

        if (decision.ReviewedAt is not null)
        {
            return Redirect(path, error: "This decision has already been reviewed.");
        }

        if (reviewOutcome == DecisionReviewOutcome.Rejected)
        {
            var rejected = await decisions.TryReviewAsync(
                decisionId,
                DecisionReviewOutcome.Rejected,
                gate.User!.Username,
                DateTimeOffset.UtcNow,
                context.RequestAborted).ConfigureAwait(false);
            if (rejected is null)
            {
                return Redirect(path, error: "This decision was reviewed by another session.");
            }

            await configAuditor.RecordDecisionReviewAsync(
                gate.User.Username,
                decisionId,
                DecisionReviewOutcome.Rejected,
                ip: null,
                duration: null,
                actionId: null,
                outcome: "Rejected",
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(path, status: "Decision rejected.");
        }

        var resolution = await targetResolver.ResolveAsync(decision, context.RequestAborted).ConfigureAwait(false);
        if (resolution.Target.Kind != IncidentTargetIpKind.Found || string.IsNullOrWhiteSpace(resolution.Target.Ip))
        {
            return Redirect(path, error: $"Approval unavailable for this decision: {resolution.Target.Detail}");
        }

        if (!TryReadDuration(form["duration"].ToString(), out var duration, out error))
        {
            return Redirect(path, error: error);
        }

        var approved = await decisions.TryReviewAsync(
            decisionId,
            DecisionReviewOutcome.Approved,
            gate.User!.Username,
            DateTimeOffset.UtcNow,
            context.RequestAborted).ConfigureAwait(false);
        if (approved is null)
        {
            return Redirect(path, error: "This decision was reviewed by another session.");
        }

        var timeout = MikroTikBanActionProvider.FormatRouterOsDuration(duration.Duration);
        var action = new ActionRecord
        {
            Id = ViegardId.New(),
            DecisionId = decisionId,
            ProviderId = MikroTikBanActionProvider.MikroTikProviderId,
            OperationId = MikroTikBanActionProvider.BanIpOperationId,
            ParametersJson = JsonSerializer.Serialize(new BanParameters(resolution.Target.Ip, timeout), Json),
            Status = ActionStatus.Pending,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await actions.AddAsync(action, context.RequestAborted).ConfigureAwait(false);
            await actionQueue.EnqueueAsync(new ActionWorkItem(action.Id), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await configAuditor.RecordDecisionReviewAsync(
                gate.User.Username,
                decisionId,
                DecisionReviewOutcome.Approved,
                resolution.Target.Ip,
                timeout,
                action.Id,
                $"DispatchFailed: {ex.GetType().Name}",
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(path, error: "Approval was recorded, but action dispatch failed.  Check logs before retrying.");
        }

        await configAuditor.RecordDecisionReviewAsync(
            gate.User.Username,
            decisionId,
            DecisionReviewOutcome.Approved,
            resolution.Target.Ip,
            timeout,
            action.Id,
            "Queued",
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(path, status: $"Decision approved.  Ban action {action.Id:N} queued for {resolution.Target.Ip}.");
    }

    private const string BulkRejectReturnPath = "/decisions?outcome=RequireApproval";

    internal static async Task<IResult> BulkRejectAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IDecisionStore decisions,
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
            BulkRejectReturnPath,
            "bulk-rejecting decisions").ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!int.TryParse(form["maxSeverity"].ToString(), out var maxSeverity)
            || maxSeverity is < 1 or > 10)
        {
            return Redirect(BulkRejectReturnPath, error: "Max severity must be a number between 1 and 10.");
        }

        var rejected = await decisions.BulkRejectUnreviewedAsync(
            maxSeverity,
            gate.User!.Username,
            DateTimeOffset.UtcNow,
            context.RequestAborted).ConfigureAwait(false);

        await configAuditor.RecordDecisionBulkRejectAsync(
            gate.User.Username,
            maxSeverity,
            rejected,
            context.RequestAborted).ConfigureAwait(false);

        return rejected == 0
            ? Redirect(BulkRejectReturnPath, status: $"No unreviewed decisions at severity {maxSeverity} or below were found.")
            : Redirect(BulkRejectReturnPath, status: $"Rejected {rejected} decision{(rejected == 1 ? "" : "s")} at severity {maxSeverity} or below.");
    }

    internal static async Task<IResult> UnbanAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IActiveBanStore activeBans,
        IActionStore actions,
        IWorkQueue<ActionWorkItem> actionQueue,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireStepUpAsync(context, users, sessions, authAuditor, BansPath, "removing bans").ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        var ipText = form["ip"].ToString();
        if (string.IsNullOrWhiteSpace(ipText) || !IPAddress.TryParse(ipText.Trim(), out var parsed))
        {
            return Redirect(BansPath, error: "Ban IP was not valid.  Reload the page and try again.");
        }

        var ip = (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString();
        ActiveBan activeBan;
        try
        {
            var found = await activeBans.GetByIpAsync(ip, context.RequestAborted).ConfigureAwait(false);
            if (found is null || found.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return Redirect(BansPath, error: "No active ban exists for that IP.");
            }

            activeBan = found;
        }
        catch (FormatException)
        {
            return Redirect(BansPath, error: "Ban IP was not valid.  Reload the page and try again.");
        }

        var action = new ActionRecord
        {
            Id = ViegardId.New(),
            DecisionId = activeBan.DecisionId,
            ProviderId = MikroTikBanActionProvider.MikroTikProviderId,
            OperationId = MikroTikBanActionProvider.RemoveBanOperationId,
            ParametersJson = JsonSerializer.Serialize(new RemoveBanParameters(ip), Json),
            Status = ActionStatus.Pending,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await actions.AddAsync(action, context.RequestAborted).ConfigureAwait(false);
            await actionQueue.EnqueueAsync(new ActionWorkItem(action.Id), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await configAuditor.RecordUnbanAsync(
                gate.User!.Username,
                ip,
                activeBan.DecisionId,
                action.Id,
                $"DispatchFailed: {ex.GetType().Name}",
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(BansPath, error: "Unban request could not be dispatched.  Check logs before retrying.");
        }

        await configAuditor.RecordUnbanAsync(
            gate.User!.Username,
            ip,
            activeBan.DecisionId,
            action.Id,
            "Queued",
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(BansPath, status: $"Unban action {action.Id:N} queued for {ip}.");
    }

    private static bool TryReadDecisionId(IFormCollection form, out Guid id, out string error)
    {
        error = string.Empty;
        if (!Guid.TryParse(form["id"].ToString(), out id) || id == Guid.Empty)
        {
            error = "Decision id was not valid.  Reload the page and try again.";
            return false;
        }

        return true;
    }

    private static Guid ReadDecisionIdOrEmpty(IFormCollection form) =>
        Guid.TryParse(form["id"].ToString(), out var id) ? id : Guid.Empty;

    private static bool TryReadVerdict(string value, out DecisionReviewOutcome outcome, out string error)
    {
        if (string.Equals(value, "approve", StringComparison.Ordinal))
        {
            outcome = DecisionReviewOutcome.Approved;
            error = string.Empty;
            return true;
        }

        if (string.Equals(value, "reject", StringComparison.Ordinal))
        {
            outcome = DecisionReviewOutcome.Rejected;
            error = string.Empty;
            return true;
        }

        outcome = default;
        error = "Review verdict was not valid.  Reload the page and try again.";
        return false;
    }

    private static bool TryReadDuration(string value, out DecisionReviewDurationOption duration, out string error)
    {
        if (DecisionReviewDurations.TryGet(value, out duration))
        {
            error = string.Empty;
            return true;
        }

        duration = default!;
        error = "Approval duration must be 1d, 7d, or 30d.";
        return false;
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
        string returnPath,
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
        return new GateResult(user, Redirect(returnPath, error: $"Step-up verification is required before {actionLabel}."));
    }

    private static async ValueTask<Viegard.Domain.Admin.AdminUser?> GetCurrentUserAsync(HttpContext context, IAdminUserStore users)
    {
        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out var userId)
            ? await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false)
            : null;
    }

    private static IResult Redirect(string path, string? status = null, string? error = null) =>
        Results.Redirect(path).WithFlash(status: status, error: error);

    private static string DecisionDetailPath(Guid id) =>
        id == Guid.Empty ? DecisionsPath : $"/decisions/{id:N}";

    private sealed record GateResult(Viegard.Domain.Admin.AdminUser? User, IResult? Failure);

    private sealed record BanParameters(string Ip, string Timeout);

    private sealed record RemoveBanParameters(string Ip);
}
