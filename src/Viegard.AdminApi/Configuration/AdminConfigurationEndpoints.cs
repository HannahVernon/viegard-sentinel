using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Viegard.AdminApi.Auth;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Configuration;

public static class AdminConfigurationEndpoints
{
    private const string ConfigurationPath = "/configuration#retention";

    public static void MapAdminConfigurationEndpoints(this WebApplication app)
    {
        app.MapPost("/configuration/retention", SaveRetentionAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }

    internal static async Task<IResult> SaveRetentionAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IRetentionSettingsStore retentionSettings,
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
            return Redirect(error: "Step-up verification is required before editing retention settings.");
        }

        if (!int.TryParse(form["version"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var expectedVersion)
            || expectedVersion < 0)
        {
            return Redirect(error: "Retention settings version was not valid.  Reload the page and try again.");
        }

        var before = await retentionSettings.GetAsync(context.RequestAborted).ConfigureAwait(false);
        if (!TryReadSettings(form, before, out var candidate, out var error))
        {
            return Redirect(error: error);
        }

        var result = await retentionSettings.UpsertAsync(
            candidate,
            expectedVersion,
            user.Username,
            DateTimeOffset.UtcNow,
            context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Redirect(error: "Retention settings were changed by another session.  Review the current values and save again.");
        }

        await configAuditor.RecordRetentionSettingsWriteAsync(
            user.Username,
            before,
            result.Settings,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(status: "Retention settings saved.");
    }

    private static bool TryReadSettings(
        IFormCollection form,
        RetentionSettings? current,
        out RetentionSettings settings,
        out string error)
    {
        settings = current ?? new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            Version = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "admin",
        };
        error = string.Empty;

        foreach (var descriptor in RetentionTargetMetadata.Descriptors)
        {
            var value = form[descriptor.SettingName].ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                settings = settings.WithDays(descriptor.Target, null);
                continue;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
                || days < 0)
            {
                error = RetentionSettingsValidator.PeriodError;
                return false;
            }

            settings = settings.WithDays(descriptor.Target, days);
        }

        return true;
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

    private static IResult Redirect(string? status = null, string? error = null) =>
        Results.Redirect(AdminAuthEndpoints.BuildRedirectPath(ConfigurationPath, status, error));
}
