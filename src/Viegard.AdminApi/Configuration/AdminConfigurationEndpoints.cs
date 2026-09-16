using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Viegard.AdminApi.Auth;
using Viegard.Application.Configuration;
using Viegard.Application.Policy;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Configuration;

public static class AdminConfigurationEndpoints
{
    private const string RetentionConfigurationPath = "/configuration#retention";
    private const string SatellitesConfigurationPath = "/configuration#satellites";
    private const string RoutersConfigurationPath = "/configuration#routers";
    private const string UpgradesConfigurationPath = "/configuration#upgrades";
    private const string ThresholdsConfigurationPath = "/configuration#thresholds";
    private const string IngestionConfigurationPath = "/configuration#ingestion";

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
        app.MapPost("/configuration/routers/create", CreateRouterAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/routers/update", UpdateRouterAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/routers/toggle", ToggleRouterAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/routers/delete", DeleteRouterAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/routers/test", TestRouterAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/routers/fetch-certificate", FetchRouterCertificateAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/upgrades/request", RequestHostUpgradeAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/thresholds", SavePolicyThresholdsAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/configuration/ingestion", SaveIngestionFiltersAsync)
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

    internal static async Task<IResult> CreateRouterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IMikroTikRouterStore routers,
        IRouterCredentialProtector protector,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireRouterStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!TryReadRouterForm(form, null, gate.User!.Username, DateTimeOffset.UtcNow, requirePassword: true, out var router, out var password, out var error))
        {
            return Redirect(RoutersConfigurationPath, error: error);
        }

        string passwordCiphertext;
        try
        {
            passwordCiphertext = protector.Protect(router.Id, password!);
        }
        catch (RouterCredentialProtectionException ex)
        {
            return Redirect(RoutersConfigurationPath, error: ex.Message);
        }

        var result = await routers.CreateAsync(router, passwordCiphertext, context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Redirect(RoutersConfigurationPath, error: RouterSaveError(result.Status));
        }

        await configAuditor.RecordRouterWriteAsync(
            "RouterCreated",
            gate.User.Username,
            before: null,
            after: result.Router,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(RoutersConfigurationPath, status: $"Router {result.Router!.Name} created.  Credential set.");
    }

    internal static async Task<IResult> UpdateRouterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IMikroTikRouterStore routers,
        IRouterCredentialProtector protector,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireRouterStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!TryReadRouterIdAndVersion(form, out var id, out var expectedRowVersion, out var error))
        {
            return Redirect(RoutersConfigurationPath, error: error);
        }

        var before = await routers.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (before is null)
        {
            return Redirect(RoutersConfigurationPath, error: "Router was not found.  Reload the page and try again.");
        }

        if (!TryReadRouterForm(form, before, gate.User!.Username, DateTimeOffset.UtcNow, requirePassword: false, out var router, out var password, out error))
        {
            return Redirect(RouterEditPath(id), error: error);
        }

        string? passwordCiphertext = null;
        if (password is not null)
        {
            try
            {
                passwordCiphertext = protector.Protect(router.Id, password);
            }
            catch (RouterCredentialProtectionException ex)
            {
                return Redirect(RouterEditPath(id), error: ex.Message);
            }
        }

        var result = await routers.UpdateAsync(router, expectedRowVersion, passwordCiphertext, context.RequestAborted)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Redirect(RouterEditPath(id), error: RouterSaveError(result.Status));
        }

        await configAuditor.RecordRouterWriteAsync(
            "RouterUpdated",
            gate.User.Username,
            before,
            result.Router,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(RoutersConfigurationPath, status: $"Router {result.Router!.Name} saved.  Credential {(passwordCiphertext is null ? "unchanged" : "updated")}.");
    }

    internal static async Task<IResult> ToggleRouterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IMikroTikRouterStore routers,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireRouterStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!TryReadRouterIdAndVersion(form, out var id, out var expectedRowVersion, out var error))
        {
            return Redirect(RoutersConfigurationPath, error: error);
        }

        if (!bool.TryParse(form["enabled"].ToString(), out var enabled))
        {
            return Redirect(RoutersConfigurationPath, error: "Router enabled state was not valid.  Reload the page and try again.");
        }

        var before = await routers.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (before is null)
        {
            return Redirect(RoutersConfigurationPath, error: "Router was not found.  Reload the page and try again.");
        }

        var candidate = before with
        {
            Enabled = enabled,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = gate.User!.Username,
        };
        var result = await routers.UpdateAsync(candidate, expectedRowVersion, passwordCiphertext: null, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Redirect(RoutersConfigurationPath, error: RouterSaveError(result.Status));
        }

        await configAuditor.RecordRouterWriteAsync(
            enabled ? "RouterEnabled" : "RouterDisabled",
            gate.User.Username,
            before,
            result.Router,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(RoutersConfigurationPath, status: $"Router {result.Router!.Name} {(enabled ? "enabled" : "disabled")}.");
    }

    internal static async Task<IResult> DeleteRouterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IMikroTikRouterStore routers,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireRouterStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!TryReadRouterIdAndVersion(form, out var id, out var expectedRowVersion, out var error))
        {
            return Redirect(RoutersConfigurationPath, error: error);
        }

        var before = await routers.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        var result = await routers.DeleteAsync(id, expectedRowVersion, context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Redirect(RoutersConfigurationPath, error: result.Status == MikroTikRouterDeleteStatus.Conflict
                ? "Router was changed by another session.  Review the current values and try again."
                : "Router was not found.  Reload the page and try again.");
        }

        await configAuditor.RecordRouterWriteAsync(
            "RouterDeleted",
            gate.User!.Username,
            before,
            after: null,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(RoutersConfigurationPath, status: before is null ? "Router deleted." : $"Router {before.Name} deleted.");
    }

    internal static async Task<IResult> TestRouterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IMikroTikRouterStore routers,
        RouterConnectivityTester tester,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireRouterStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!TryReadRouterId(form, out var id, out var error))
        {
            return Redirect(RoutersConfigurationPath, error: error);
        }

        var router = await routers.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (router is null)
        {
            return Redirect(RoutersConfigurationPath, error: "Router was not found.  Reload the page and try again.");
        }

        var credential = await routers.GetCredentialCiphertextAsync(id, context.RequestAborted).ConfigureAwait(false);
        var result = await tester.TestAsync(router, credential ?? string.Empty, context.RequestAborted).ConfigureAwait(false);
        await configAuditor.RecordRouterProbeAsync(
            "router-probe",
            gate.User!.Username,
            router.Name,
            result.Message,
            context.RequestAborted).ConfigureAwait(false);

        return Redirect(
            RoutersConfigurationPath,
            status: result.Succeeded ? result.Message : null,
            error: result.Succeeded ? null : result.Message);
    }

    internal static async Task<IResult> FetchRouterCertificateAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IMikroTikRouterStore routers,
        RouterCertificateFetcher fetcher,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var gate = await RequireRouterStepUpAsync(context, users, sessions, authAuditor).ConfigureAwait(false);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!TryReadRouterId(form, out var id, out var error))
        {
            return Redirect(RoutersConfigurationPath, error: error);
        }

        var router = await routers.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (router is null)
        {
            return Redirect(RoutersConfigurationPath, error: "Router was not found.  Reload the page and try again.");
        }

        if (router.TransportMode == MikroTikRouterTransportMode.PlainHttp)
        {
            return Redirect(RoutersConfigurationPath, error: "Certificate fetch is available only for HTTPS routers.");
        }

        try
        {
            var certificate = await fetcher.FetchAsync(router.BaseUrl, context.RequestAborted).ConfigureAwait(false);
            var message =
                $"Certificate fetched for {router.Name}: subject {OneLine(certificate.Subject, 120)}; issuer {OneLine(certificate.Issuer, 120)}; expires {certificate.NotAfter.UtcDateTime:u}; SHA-256 {certificate.FingerprintDisplay}.  Save the router with HTTPS pinned to pin this fingerprint.";
            await configAuditor.RecordRouterProbeAsync(
                "router-probe",
                gate.User!.Username,
                router.Name,
                $"certificate-fetch {certificate.Sha256Fingerprint}",
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(RouterEditPath(router.Id, certificate.Sha256Fingerprint), status: message);
        }
        catch (RouterProbeException ex)
        {
            await configAuditor.RecordRouterProbeAsync(
                "router-probe",
                gate.User!.Username,
                router.Name,
                ex.Message,
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(RoutersConfigurationPath, error: ex.Message);
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

    internal static async Task<IResult> SaveIngestionFiltersAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IIngestionFilterStore ingestionFilters,
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
            return Redirect(IngestionConfigurationPath, error: "Step-up verification is required before editing ingestion filters.");
        }

        if (!TryReadMdaemonIngestionFilterMatrix(form, out var matrix, out var error))
        {
            return Redirect(IngestionConfigurationPath, error: error);
        }

        try
        {
            var result = await ingestionFilters.SaveMatrixAsync(
                MDaemonIngestionFilterPolicy.SourceType,
                matrix,
                MDaemonIngestionFilterPolicy.LockedEventKindNames,
                user.Username,
                DateTimeOffset.UtcNow,
                context.RequestAborted).ConfigureAwait(false);
            await configAuditor.RecordIngestionFiltersWriteAsync(
                user.Username,
                MDaemonIngestionFilterPolicy.SourceType,
                result.Before,
                result.After,
                context.RequestAborted).ConfigureAwait(false);
            return Redirect(IngestionConfigurationPath, status: "Ingestion filters saved.");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect(IngestionConfigurationPath, error: ex.Message);
        }
    }

    internal static async Task<IResult> SavePolicyThresholdsAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IPolicyThresholdSettingsStore policyThresholds,
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
            return Redirect(ThresholdsConfigurationPath, error: "Step-up verification is required before editing policy thresholds.");
        }

        if (!int.TryParse(form["rowVersion"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var expectedRowVersion)
            || expectedRowVersion < 0)
        {
            return Redirect(ThresholdsConfigurationPath, error: "Policy threshold settings version was not valid.  Reload the page and try again.");
        }

        var before = await policyThresholds.GetAsync(context.RequestAborted).ConfigureAwait(false);
        if (!TryReadPolicyThresholdSettings(form, before, out var candidate, out var error))
        {
            return Redirect(ThresholdsConfigurationPath, error: error);
        }

        var result = await policyThresholds.UpdateAsync(
            candidate,
            expectedRowVersion,
            user.Username,
            DateTimeOffset.UtcNow,
            context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Redirect(ThresholdsConfigurationPath, error: "Policy threshold settings were changed by another session.  Review the current values and save again.");
        }

        await configAuditor.RecordPolicyThresholdSettingsWriteAsync(
            user.Username,
            before,
            result.Settings,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect(ThresholdsConfigurationPath, status: "Policy threshold settings saved.");
    }

    private static bool TryReadRouterForm(
        IFormCollection form,
        MikroTikRouter? existing,
        string updatedBy,
        DateTimeOffset updatedAt,
        bool requirePassword,
        out MikroTikRouter router,
        out string? password,
        out string error)
    {
        router = existing ?? new MikroTikRouter
        {
            Id = Viegard.Domain.ViegardId.New(),
            Name = string.Empty,
            BaseUrl = "http://localhost",
            TransportMode = MikroTikRouterTransportMode.PlainHttp,
            Username = string.Empty,
            Enabled = true,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            UpdatedBy = updatedBy,
            RowVersion = 0,
        };
        password = null;
        error = string.Empty;

        if (!MikroTikRouterValidator.TryParseTransportMode(form["transportMode"].ToString(), out var transportMode, out error)
            || !MikroTikRouterValidator.TryNormalizeName(form["name"].ToString(), out var name, out error)
            || !MikroTikRouterValidator.TryNormalizeBaseUrl(form["baseUrl"].ToString(), transportMode, out var baseUrl, out error)
            || !MikroTikRouterValidator.TryNormalizePinnedCertificateSha256(form["pinnedCertificateSha256"].ToString(), transportMode, out var pinned, out error)
            || !MikroTikRouterValidator.TryNormalizeUsername(form["username"].ToString(), out var username, out error))
        {
            return false;
        }

        var passwordValue = form["password"].ToString();
        if (requirePassword && string.IsNullOrEmpty(passwordValue))
        {
            error = "Router password is required.";
            return false;
        }

        if (!string.IsNullOrEmpty(passwordValue))
        {
            password = passwordValue;
        }

        router = router with
        {
            Name = name,
            BaseUrl = baseUrl,
            TransportMode = transportMode,
            PinnedCertificateSha256 = pinned,
            Username = username,
            Enabled = form.ContainsKey("enabled"),
            UpdatedAt = updatedAt.ToUniversalTime(),
            UpdatedBy = MikroTikRouterValidator.NormalizeUpdatedBy(updatedBy),
        };
        return true;
    }

    private static bool TryReadRouterId(IFormCollection form, out Guid id, out string error)
    {
        error = string.Empty;
        if (!Guid.TryParse(form["id"].ToString(), out id) || id == Guid.Empty)
        {
            error = "Router id was not valid.  Reload the page and try again.";
            return false;
        }

        return true;
    }

    private static bool TryReadRouterIdAndVersion(IFormCollection form, out Guid id, out int rowVersion, out string error)
    {
        if (!TryReadRouterId(form, out id, out error))
        {
            rowVersion = 0;
            return false;
        }

        if (!int.TryParse(form["rowVersion"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out rowVersion)
            || rowVersion < 0)
        {
            error = "Router version was not valid.  Reload the page and try again.";
            return false;
        }

        return true;
    }

    private static string RouterSaveError(MikroTikRouterSaveStatus status) =>
        status switch
        {
            MikroTikRouterSaveStatus.Conflict =>
                "Router was changed by another session.  Review the current values and save again.",
            MikroTikRouterSaveStatus.DuplicateName =>
                "A router with that name already exists.  Router names must be unique.",
            MikroTikRouterSaveStatus.NotFound =>
                "Router was not found.  Reload the page and try again.",
            _ => "Router could not be saved.",
        };

    private static string RouterEditPath(Guid id, string? pinnedCertificateSha256 = null)
    {
        var path = $"/configuration?editRouter={id:N}";
        if (!string.IsNullOrWhiteSpace(pinnedCertificateSha256))
        {
            path += $"&routerPin={Uri.EscapeDataString(pinnedCertificateSha256)}";
        }

        return path + "#routers";
    }

    private static string OneLine(string? value, int maxChars)
    {
        var sanitized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maxChars ? sanitized : sanitized[..maxChars];
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

    private static bool TryReadPolicyThresholdSettings(
        IFormCollection form,
        PolicyThresholdSettings? current,
        out PolicyThresholdSettings settings,
        out string error)
    {
        settings = current ?? new PolicyThresholdSettings
        {
            Id = PolicyThresholdSettings.FixedId,
            RowVersion = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "admin",
        };
        error = string.Empty;

        if (!double.TryParse(
                form["reviewConfidence"].ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var reviewConfidence)
            || !double.TryParse(
                form["actionConfidence"].ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var actionConfidence))
        {
            error = PolicyThresholdSettingsValidator.ConfidenceBoundsError;
            return false;
        }

        if (!int.TryParse(
                form["actionMinSeverity"].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var actionMinSeverity))
        {
            error = PolicyThresholdSettingsValidator.SeverityRangeError;
            return false;
        }

        settings = settings with
        {
            ReviewConfidence = reviewConfidence,
            ActionConfidence = actionConfidence,
            ActionMinSeverity = actionMinSeverity,
        };

        if (!PolicyThresholdSettingsValidator.TryValidate(settings, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadMdaemonIngestionFilterMatrix(
        IFormCollection form,
        out IReadOnlyDictionary<string, bool> matrix,
        out string error)
    {
        var requestedSuppression = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in form["suppressed"])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                requestedSuppression.Add(value);
            }
        }

        var rows = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var descriptor in MDaemonIngestionFilterPolicy.Descriptors)
        {
            var suppressed = requestedSuppression.Contains(descriptor.EventKindName);
            if (descriptor.Locked && suppressed)
            {
                matrix = rows;
                error = $"MDaemon event kind {descriptor.EventKindName} is locked and cannot be suppressed.";
                return false;
            }

            rows[descriptor.EventKindName] = !descriptor.Locked && suppressed;
        }

        matrix = rows;
        error = string.Empty;
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

    private static async ValueTask<RouterStepUpResult> RequireRouterStepUpAsync(
        HttpContext context,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor)
    {
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return new RouterStepUpResult(null, Results.Redirect("/login"));
        }

        if (await AdminStepUpGate.HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            return new RouterStepUpResult(user, null);
        }

        await authAuditor.RecordAsync(
            AdminAuthEventKind.StepUpFailed,
            user.Username,
            context,
            enqueueForCorrelation: true,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
        return new RouterStepUpResult(
            user,
            Redirect(RoutersConfigurationPath, error: "Step-up verification is required before managing routers."));
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

    private sealed record RouterStepUpResult(Viegard.Domain.Admin.AdminUser? User, IResult? Failure);
}
