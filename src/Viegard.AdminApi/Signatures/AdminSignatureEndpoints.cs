using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Viegard.AdminApi.Auth;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Configuration;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Signatures;

public static class AdminSignatureEndpoints
{
    public static void MapAdminSignatureEndpoints(this WebApplication app)
    {
        app.MapPost("/signatures/save", SaveAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/signatures/toggle", ToggleAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/signatures/delete", DeleteAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }

    private static async Task<IResult> SaveAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ICustomSignatureStore signatures,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
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
            return Redirect("/signatures", error: "Step-up verification is required before editing signatures.");
        }

        if (!TryReadSignature(form, user.Username, out var signature, out var error))
        {
            return Redirect("/signatures#add", error: error);
        }

        var before = await signatures.GetAsync(signature.Id, context.RequestAborted).ConfigureAwait(false);
        try
        {
            // D-0029 configuration writes go directly to the runtime config
            // store and publish NOTIFY.  The D-0011 command queue remains for
            // commands that the pipeline must execute, not admin-owned config.
            var saved = await signatures.UpsertAsync(signature, context.RequestAborted).ConfigureAwait(false);
            await configAuditor.RecordSignatureWriteAsync(
                before is null ? "created" : "updated",
                user.Username,
                before,
                saved,
                context.RequestAborted).ConfigureAwait(false);
            return Redirect("/signatures", status: "Signature saved.");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect("/signatures", error: ex.Message);
        }
    }

    private static async Task<IResult> ToggleAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ICustomSignatureStore signatures,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
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
            return Redirect("/signatures", error: "Step-up verification is required before changing signatures.");
        }

        if (!Guid.TryParse(form["id"].ToString(), out var id))
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        var before = await signatures.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (before is null)
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        var saved = await signatures.UpsertAsync(before with
        {
            Enabled = !before.Enabled,
            UpdatedBy = user.Username,
        }, context.RequestAborted).ConfigureAwait(false);
        await configAuditor.RecordSignatureWriteAsync(
            saved.Enabled ? "enabled" : "disabled",
            user.Username,
            before,
            saved,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect("/signatures", status: saved.Enabled ? "Signature enabled." : "Signature disabled.");
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ICustomSignatureStore signatures,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
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
            return Redirect("/signatures", error: "Step-up verification is required before deleting signatures.");
        }

        if (!Guid.TryParse(form["id"].ToString(), out var id))
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        var removed = await signatures.DeleteAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (removed is null)
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        await configAuditor.RecordSignatureWriteAsync(
            "deleted",
            user.Username,
            removed,
            after: null,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect("/signatures", status: "Signature deleted.");
    }

    private static bool TryReadSignature(
        IFormCollection form,
        string username,
        out CustomSignature signature,
        out string error)
    {
        signature = new CustomSignature
        {
            Id = ViegardId.New(),
            Name = string.Empty,
            Enabled = false,
            Target = CustomSignatureTarget.HttpUri,
            MatchType = CustomSignatureMatchType.Contains,
            Pattern = string.Empty,
            Category = string.Empty,
            Severity = 0,
            EvidenceWeight = 1.0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = username,
            Version = 0,
        };
        error = string.Empty;

        var idText = form["id"].ToString();
        var id = string.IsNullOrWhiteSpace(idText) ? ViegardId.New() : Guid.Empty;
        if (!string.IsNullOrWhiteSpace(idText) && !Guid.TryParse(idText, out id))
        {
            error = "Signature not found.";
            return false;
        }

        var target = Enum.TryParse<CustomSignatureTarget>(form["target"].ToString(), ignoreCase: true, out var parsedTarget)
            ? parsedTarget
            : (CustomSignatureTarget)(-1);
        var matchType = Enum.TryParse<CustomSignatureMatchType>(form["matchType"].ToString(), ignoreCase: true, out var parsedMatch)
            ? parsedMatch
            : (CustomSignatureMatchType)(-1);
        var severity = int.TryParse(form["severity"].ToString(), out var parsedSeverity)
            ? parsedSeverity
            : -1;
        var evidenceWeight = double.TryParse(
            form["evidenceWeight"].ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedWeight)
            ? parsedWeight
            : double.NaN;

        signature = new CustomSignature
        {
            Id = id,
            Name = form["name"].ToString(),
            Enabled = string.Equals(form["enabled"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(form["enabled"].ToString(), "on", StringComparison.OrdinalIgnoreCase),
            Target = target,
            MatchType = matchType,
            Pattern = form["pattern"].ToString(),
            Category = form["category"].ToString(),
            Severity = severity,
            EvidenceWeight = evidenceWeight,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = username,
            Version = 0,
        };
        signature = CustomSignatureValidator.Normalize(signature);
        var validation = CustomSignatureValidator.Validate(signature);
        if (validation.IsValid)
        {
            return true;
        }

        error = CustomSignatureValidator.UniformError(validation);
        return false;
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        return await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async ValueTask<Viegard.Domain.Admin.AdminUser?> GetCurrentUserAsync(HttpContext context, IAdminUserStore users)
    {
        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out var userId)
            ? await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false)
            : null;
    }

    private static IResult Redirect(string path, string? status = null, string? error = null) =>
        Results.Redirect(BuildRedirectPath(path, status, error));

    private static string BuildRedirectPath(string path, string? status = null, string? error = null)
    {
        var query = status is not null
            ? $"status={Uri.EscapeDataString(status)}"
            : error is not null
                ? $"error={Uri.EscapeDataString(error)}"
                : string.Empty;
        return string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
    }
}
