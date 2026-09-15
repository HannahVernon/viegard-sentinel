using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Viegard.AdminApi.Auth;
using Viegard.Application.Configuration;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Configuration;

public static class AdminConfigurationEndpoints
{
    private const string RetentionConfigurationPath = "/configuration#retention";
    private const string SatellitesConfigurationPath = "/configuration#satellites";
    private const string UpgradesConfigurationPath = "/configuration#upgrades";

    public static void MapAdminConfigurationEndpoints(this WebApplication app)
    {
        app.MapPost("/configuration/retention", SaveRetentionAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/satellites/create", CreateSatelliteAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/satellites/rotate", RotateSatellitePasswordAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/satellites/revoke", RevokeSatelliteAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/upgrades/request", RequestHostUpgradeAsync)
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
            return Redirect(RetentionConfigurationPath, error: "Step-up verification is required before editing retention settings.");
        }

        if (!int.TryParse(form["version"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var expectedVersion)
            || expectedVersion < 0)
        {
            return Redirect(RetentionConfigurationPath, error: "Retention settings version was not valid.  Reload the page and try again.");
        }

        var before = await retentionSettings.GetAsync(context.RequestAborted).ConfigureAwait(false);
        if (!TryReadSettings(form, before, out var candidate, out var error))
        {
            return Redirect(RetentionConfigurationPath, error: error);
        }

        var result = await retentionSettings.UpsertAsync(
            candidate,
            expectedVersion,
            user.Username,
            DateTimeOffset.UtcNow,
            context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Redirect(RetentionConfigurationPath, error: "Retention settings were changed by another session.  Review the current values and save again.");
        }

        await configAuditor.RecordRetentionSettingsWriteAsync(
            user.Username,
            before,
            result.Settings,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(RetentionConfigurationPath, status: "Retention settings saved.");
    }

    internal static async Task<IResult> CreateSatelliteAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ISatelliteRoleStore satelliteRoles,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor,
        SatelliteRoleCredentialCookie credentialCookie)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireSatelliteStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!SatelliteRoleName.TryNormalize(form["name"].ToString(), out var normalized, out var error))
        {
            return Redirect(SatellitesConfigurationPath, error: error);
        }

        try
        {
            var credential = await satelliteRoles.CreateAsync(normalized.SatelliteName, context.RequestAborted).ConfigureAwait(false);
            credentialCookie.Write(context, credential);
            await configAuditor.RecordSatelliteRoleWriteAsync(
                "SatelliteRoleCreated",
                gate.User!.Username,
                credential.RoleName,
                credential.GrantSummary,
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(SatellitesConfigurationPath, status: $"Satellite role {credential.RoleName} created.  Copy the password now.");
        }
        catch (SatelliteRoleStoreException ex)
        {
            return Redirect(SatellitesConfigurationPath, error: ex.Message);
        }
    }

    internal static async Task<IResult> RotateSatellitePasswordAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ISatelliteRoleStore satelliteRoles,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor,
        SatelliteRoleCredentialCookie credentialCookie)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireSatelliteStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!SatelliteRoleName.TryNormalize(form["name"].ToString(), out var normalized, out var error))
        {
            return Redirect(SatellitesConfigurationPath, error: error);
        }

        try
        {
            var credential = await satelliteRoles.RotatePasswordAsync(normalized.SatelliteName, context.RequestAborted).ConfigureAwait(false);
            credentialCookie.Write(context, credential);
            await configAuditor.RecordSatelliteRoleWriteAsync(
                "SatelliteRolePasswordRotated",
                gate.User!.Username,
                credential.RoleName,
                credential.GrantSummary,
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(SatellitesConfigurationPath, status: $"Password rotated for {credential.RoleName}.  Copy the password now.");
        }
        catch (SatelliteRoleStoreException ex)
        {
            return Redirect(SatellitesConfigurationPath, error: ex.Message);
        }
    }

    internal static async Task<IResult> RevokeSatelliteAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ISatelliteRoleStore satelliteRoles,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireSatelliteStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!SatelliteRoleName.TryNormalize(form["name"].ToString(), out var normalized, out var error))
        {
            return Redirect(SatellitesConfigurationPath, error: error);
        }

        try
        {
            await satelliteRoles.RevokeAsync(normalized.SatelliteName, context.RequestAborted).ConfigureAwait(false);
            await configAuditor.RecordSatelliteRoleWriteAsync(
                "SatelliteRoleRevoked",
                gate.User!.Username,
                normalized.RoleName,
                "DROP OWNED BY and DROP ROLE executed; grants revoked.",
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(SatellitesConfigurationPath, status: $"Satellite role {normalized.RoleName} revoked.");
        }
        catch (SatelliteRoleStoreException ex)
        {
            return Redirect(SatellitesConfigurationPath, error: ex.Message);
        }
    }

    internal static async Task<IResult> RequestHostUpgradeAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IHostUpgradeCommandStore hostUpgrades,
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
            return Redirect(UpgradesConfigurationPath, error: "Step-up verification is required before requesting a host upgrade.");
        }

        var target = form["target"].ToString();
        if (!string.Equals(target, HostUpgradeCommandPolicy.DefaultTarget, StringComparison.Ordinal))
        {
            return Redirect(UpgradesConfigurationPath, error: "Only the vm upgrade target is available in this version.");
        }

        try
        {
            var command = await hostUpgrades.RequestAsync(
                HostUpgradeCommandPolicy.DefaultTarget,
                user.Username,
                context.RequestAborted).ConfigureAwait(false);
            await configAuditor.RecordHostUpgradeRequestedAsync(
                user.Username,
                command.Target,
                command.Id,
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(UpgradesConfigurationPath, status: $"Upgrade request {command.Id:N} was queued for {command.Target}.");
        }
        catch (HostUpgradeCommandRejectedException ex)
        {
            return Redirect(UpgradesConfigurationPath, error: HostUpgradeRejectionMessage(ex));
        }
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

    private static async ValueTask<SatelliteStepUpResult> RequireSatelliteStepUpAsync(
        HttpContext context,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor)
    {
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return new SatelliteStepUpResult(null, Results.Redirect("/login"));
        }

        if (await AdminStepUpGate.HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            return new SatelliteStepUpResult(user, null);
        }

        await authAuditor.RecordAsync(
            AdminAuthEventKind.StepUpFailed,
            user.Username,
            context,
            enqueueForCorrelation: true,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
        return new SatelliteStepUpResult(
            user,
            Redirect(SatellitesConfigurationPath, error: "Step-up verification is required before managing satellite roles."));
    }

    private static IResult Redirect(string path, string? status = null, string? error = null) =>
        Results.Redirect(AdminAuthEndpoints.BuildRedirectPath(path, status, error));

    private static string HostUpgradeRejectionMessage(HostUpgradeCommandRejectedException exception) =>
        exception.Reason switch
        {
            HostUpgradeCommandRejectionReason.SingleFlight =>
                $"An upgrade for {exception.Target} is already pending or running.  Wait for it to finish before requesting another.",
            HostUpgradeCommandRejectionReason.Cooldown =>
                $"The latest upgrade for {exception.Target} finished less than {HostUpgradeCommandPolicy.CooldownMinutes.ToString(CultureInfo.InvariantCulture)} minutes ago.  Wait a few minutes before requesting another.",
            _ => "The upgrade request could not be queued.",
        };

    private sealed record SatelliteStepUpResult(Viegard.Domain.Admin.AdminUser? User, IResult? Failure);
}
