using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Viegard.AdminApi.Auth;
using Viegard.AdminApi.Configuration;
using Viegard.Application.Audit;
using Viegard.Application.Auth;
using Viegard.Application.Configuration;
using Viegard.Application.Policy;
using Viegard.Application.Queues;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Domain.Events;
using Viegard.Domain.Health;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class AdminConfigurationEndpointsTests
{
    [Fact]
    public async Task Save_retention_rejects_negative_period_without_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.RetentionSettings.UpsertAsync(
            SettingsWith((RetentionTarget.Events, 90)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = Form(version: 1, (RetentionTarget.Events, -1));

        var result = await fixture.InvokeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains(Uri.EscapeDataString(RetentionSettingsValidator.PeriodError), location, StringComparison.Ordinal);
        var settings = await fixture.RetentionSettings.GetAsync();
        Assert.Equal(90, settings!.EventsDays);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_retention_returns_friendly_error_on_version_conflict()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.RetentionSettings.UpsertAsync(
            SettingsWith((RetentionTarget.Events, 90)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = Form(version: 0, (RetentionTarget.Events, 30));

        var result = await fixture.InvokeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Retention%20settings%20were%20changed", location, StringComparison.Ordinal);
        var settings = await fixture.RetentionSettings.GetAsync();
        Assert.Equal(90, settings!.EventsDays);
        Assert.Equal(1, settings.Version);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_retention_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        await fixture.RetentionSettings.UpsertAsync(
            SettingsWith((RetentionTarget.Events, 90)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = Form(version: 1, (RetentionTarget.Events, 30));

        var result = await fixture.InvokeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        var settings = await fixture.RetentionSettings.GetAsync();
        Assert.Equal(90, settings!.EventsDays);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_retention_persists_valid_periods_and_writes_config_audit()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.RetentionSettings.UpsertAsync(
            SettingsWith((RetentionTarget.Events, 90), (RetentionTarget.AuditRecords, 365)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = Form(
            version: 1,
            (RetentionTarget.Events, 30),
            (RetentionTarget.AuditRecords, null));

        var result = await fixture.InvokeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Retention%20settings%20saved", location, StringComparison.Ordinal);
        var settings = await fixture.RetentionSettings.GetAsync();
        Assert.Equal(30, settings!.EventsDays);
        Assert.Null(settings.AuditRecordsDays);
        Assert.Equal(2, settings.Version);
        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("changed retention settings", audit.Summary, StringComparison.Ordinal);
        Assert.Contains("RetentionSettingsChanged", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"eventsDays\":90", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"eventsDays\":30", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_jetpack_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        await fixture.JetPackSettings.UpsertAsync(
            JetPackSettings(enabled: true),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = JetPackForm(version: 1, enabled: false);

        var result = await fixture.InvokeSaveJetPackAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.True((await fixture.JetPackSettings.GetAsync())!.Enabled);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_jetpack_rejects_invalid_feed_url_without_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = JetPackForm(version: 0, feedUrl: "not-a-url");

        var result = await fixture.InvokeSaveJetPackAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains(Uri.EscapeDataString(JetPackFeedSettingsValidator.FeedUrlError), location, StringComparison.Ordinal);
        Assert.Null(await fixture.JetPackSettings.GetAsync());
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_jetpack_persists_values_and_writes_config_audit()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.JetPackSettings.UpsertAsync(
            JetPackSettings(),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = JetPackForm(
            version: 1,
            feedUrl: "https://jetpack.com/ips-v4.json",
            fetchIntervalMinutes: 90,
            enabled: false,
            addressListName: "jetpack_custom");

        var result = await fixture.InvokeSaveJetPackAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("JetPack%20allowlist%20settings%20saved", location, StringComparison.Ordinal);
        var settings = await fixture.JetPackSettings.GetAsync();
        Assert.NotNull(settings);
        Assert.Equal(TimeSpan.FromMinutes(90), settings!.FetchInterval);
        Assert.False(settings.Enabled);
        Assert.Equal("jetpack_custom", settings.AddressListName);
        Assert.Equal(2, settings.Version);
        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("JetPackFeedSettingsChanged", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"addressListName\":\"jetpack_servers\"", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"addressListName\":\"jetpack_custom\"", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_satellite_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        fixture.Context.Request.Form = SatelliteForm("mdaemon01");

        var result = await fixture.InvokeCreateSatelliteAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.SatelliteRoles.ListAsync());
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_satellite_rejects_invalid_names_without_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = SatelliteForm("bad-name");

        var result = await fixture.InvokeCreateSatelliteAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains(Uri.EscapeDataString(SatelliteRoleName.ValidationError), location, StringComparison.Ordinal);
        Assert.Empty(await fixture.SatelliteRoles.ListAsync());
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Create_satellite_persists_role_and_writes_config_audit_without_password()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = SatelliteForm("mdaemon01");

        var result = await fixture.InvokeCreateSatelliteAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Satellite%20role%20viegard_sat_mdaemon01%20created", location, StringComparison.Ordinal);
        var role = Assert.Single(await fixture.SatelliteRoles.ListAsync());
        Assert.Equal("viegard_sat_mdaemon01", role.RoleName);
        Assert.True(role.CanLogin);
        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("SatelliteRoleCreated", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("viegard_sat_mdaemon01", audit.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_router_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        fixture.Context.Request.Form = RouterCreateForm("router-a", "top-secret");

        var result = await fixture.InvokeCreateRouterAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.Routers.ListAsync());
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_router_persists_router_and_writes_config_audit_without_password()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = RouterCreateForm("router-a", "top-secret");

        var result = await fixture.InvokeCreateRouterAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Router%20router-a%20created", location, StringComparison.Ordinal);
        var router = Assert.Single(await fixture.Routers.ListAsync());
        Assert.Equal("router-a", router.Name);
        Assert.True(router.Enabled);
        Assert.StartsWith($"protected:{router.Id:N}:", await fixture.Routers.GetCredentialCiphertextAsync(router.Id), StringComparison.Ordinal);

        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("RouterCreated", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("router-a", audit.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", audit.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_router_with_blank_password_keeps_existing_credential()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var created = await fixture.Routers.CreateAsync(Router("router-a"), "cipher-existing");
        fixture.Context.Request.Form = RouterUpdateForm(created.Router!, password: "");

        var result = await fixture.InvokeUpdateRouterAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Credential%20unchanged", location, StringComparison.Ordinal);
        Assert.Equal("cipher-existing", await fixture.Routers.GetCredentialCiphertextAsync(created.Router!.Id));
        Assert.Single(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Request_host_upgrade_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        fixture.Context.Request.Form = HostUpgradeForm();

        var result = await fixture.InvokeRequestHostUpgradeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.HostUpgrades.ListRecentAsync());
    }

    [Fact]
    public async Task Request_host_upgrade_checks_step_up_before_target_validation()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        fixture.Context.Request.Form = HostUpgradeForm(newTarget: "Invalid Target");

        var result = await fixture.InvokeRequestHostUpgradeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.DoesNotContain("Target%20must%20use", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.HostUpgrades.ListRecentAsync());
    }

    [Fact]
    public async Task Save_policy_posture_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        await fixture.PolicyPosture.UpdateAsync(
            PostureSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = PostureForm(rowVersion: 1, dryRun: true, manualApprovalMode: false, emergencyStop: false);

        var result = await fixture.InvokeSavePolicyPostureAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyPosture.GetAsync();
        Assert.True(settings!.ManualApprovalMode);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_policy_posture_requires_typed_confirmation_to_disable_dry_run()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyPosture.UpdateAsync(
            PostureSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = PostureForm(rowVersion: 1, dryRun: false, manualApprovalMode: true, emergencyStop: false);

        var result = await fixture.InvokeSavePolicyPostureAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Type%20ENFORCE", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyPosture.GetAsync();
        Assert.True(settings!.DryRun);
        Assert.Equal(1, settings.RowVersion);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_policy_posture_rejects_a_wrong_confirmation_word()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyPosture.UpdateAsync(
            PostureSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = PostureForm(rowVersion: 1, dryRun: false, manualApprovalMode: true, emergencyStop: false, confirmEnforce: "enforce");

        var result = await fixture.InvokeSavePolicyPostureAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Type%20ENFORCE", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyPosture.GetAsync();
        Assert.True(settings!.DryRun);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_policy_posture_disables_dry_run_with_typed_confirmation()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyPosture.UpdateAsync(
            PostureSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = PostureForm(rowVersion: 1, dryRun: false, manualApprovalMode: true, emergencyStop: false, confirmEnforce: "ENFORCE");

        var result = await fixture.InvokeSavePolicyPostureAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Real%20enforcement%20is%20now%20active", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyPosture.GetAsync();
        Assert.False(settings!.DryRun);
        Assert.True(settings.ManualApprovalMode);
        Assert.Equal(2, settings.RowVersion);
        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("changed policy posture settings", audit.Summary, StringComparison.Ordinal);
        Assert.Contains("PolicyPostureSettingsChanged", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_policy_posture_persists_flags_without_confirmation_when_dry_run_stays_on()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyPosture.UpdateAsync(
            PostureSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = PostureForm(rowVersion: 1, dryRun: true, manualApprovalMode: false, emergencyStop: true);

        var result = await fixture.InvokeSavePolicyPostureAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Emergency%20stop%20is%20active", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyPosture.GetAsync();
        Assert.True(settings!.DryRun);
        Assert.False(settings.ManualApprovalMode);
        Assert.True(settings.EmergencyStop);
        Assert.Single(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_policy_posture_uses_environment_fallback_for_the_enforcement_check_when_unseeded()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = PostureForm(rowVersion: 0, dryRun: false, manualApprovalMode: true, emergencyStop: false);

        var result = await fixture.InvokeSavePolicyPostureAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Type%20ENFORCE", location, StringComparison.Ordinal);
        Assert.Null(await fixture.PolicyPosture.GetAsync());
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_policy_posture_returns_friendly_error_on_version_conflict()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyPosture.UpdateAsync(
            PostureSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = PostureForm(rowVersion: 0, dryRun: true, manualApprovalMode: false, emergencyStop: false);

        var result = await fixture.InvokeSavePolicyPostureAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Policy%20posture%20settings%20were%20changed", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyPosture.GetAsync();
        Assert.True(settings!.ManualApprovalMode);
        Assert.Equal(1, settings.RowVersion);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Clear_admin_errors_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        await fixture.AdminErrors.AddAsync(CapturedError());
        fixture.Context.Request.Form = new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal));

        var result = await fixture.InvokeClearErrorsAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Single(await fixture.AdminErrors.ListRecentAsync(10));
    }

    [Fact]
    public async Task Clear_admin_errors_clears_and_writes_config_audit()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.AdminErrors.AddAsync(CapturedError());
        await fixture.AdminErrors.AddAsync(CapturedError());
        fixture.Context.Request.Form = new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal));

        var result = await fixture.InvokeClearErrorsAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Cleared%202%20captured%20errors", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.AdminErrors.ListRecentAsync(10));
        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("cleared 2 captured admin errors", audit.Summary, StringComparison.Ordinal);
    }

    private static Viegard.Domain.Admin.AdminError CapturedError() => new()
    {
        Id = ViegardId.New(),
        OccurredAt = DateTimeOffset.UtcNow,
        RequestId = "00-test",
        Path = "/events",
        Method = "GET",
        Username = "hannah",
        ExceptionType = "System.TimeoutException",
        Message = "Timeout during reading attempt",
        StackTrace = "at Somewhere",
    };

    [Fact]
    public async Task Save_ingestion_filters_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        fixture.Context.Request.Form = IngestionForm(MDaemonEventKind.Other);

        var result = await fixture.InvokeSaveIngestionFiltersAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.IngestionFilters.ListAsync());
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_policy_thresholds_requires_step_up_before_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        await fixture.PolicyThresholds.UpdateAsync(
            ThresholdSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = ThresholdForm(rowVersion: 1, reviewConfidence: 0.6, actionConfidence: 0.9, severity: 7);

        var result = await fixture.InvokeSavePolicyThresholdsAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyThresholds.GetAsync();
        Assert.Equal(0.7, settings!.ReviewConfidence);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_policy_thresholds_rejects_invalid_values_without_mutating()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyThresholds.UpdateAsync(
            ThresholdSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = ThresholdForm(rowVersion: 1, reviewConfidence: 0.95, actionConfidence: 0.9, severity: 7);

        var result = await fixture.InvokeSavePolicyThresholdsAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains(Uri.EscapeDataString(PolicyThresholdSettingsValidator.ConfidenceOrderError), location, StringComparison.Ordinal);
        var settings = await fixture.PolicyThresholds.GetAsync();
        Assert.Equal(0.7, settings!.ReviewConfidence);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_policy_thresholds_returns_friendly_error_on_version_conflict()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyThresholds.UpdateAsync(
            ThresholdSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = ThresholdForm(rowVersion: 0, reviewConfidence: 0.6, actionConfidence: 0.9, severity: 7);

        var result = await fixture.InvokeSavePolicyThresholdsAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Policy%20threshold%20settings%20were%20changed", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyThresholds.GetAsync();
        Assert.Equal(0.7, settings!.ReviewConfidence);
        Assert.Equal(1, settings.RowVersion);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_policy_thresholds_persists_values_and_writes_config_audit()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.PolicyThresholds.UpdateAsync(
            ThresholdSettings(),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: fixture.Now);
        fixture.Context.Request.Form = ThresholdForm(rowVersion: 1, reviewConfidence: 0.6, actionConfidence: 0.95, severity: 8);

        var result = await fixture.InvokeSavePolicyThresholdsAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Policy%20threshold%20settings%20saved", location, StringComparison.Ordinal);
        var settings = await fixture.PolicyThresholds.GetAsync();
        Assert.Equal(0.6, settings!.ReviewConfidence);
        Assert.Equal(0.95, settings.ActionConfidence);
        Assert.Equal(8, settings.ActionMinSeverity);
        Assert.Equal(2, settings.RowVersion);
        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("changed policy threshold settings", audit.Summary, StringComparison.Ordinal);
        Assert.Contains("PolicyThresholdSettingsChanged", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"reviewConfidence\":0.7", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"reviewConfidence\":0.6", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_host_upgrade_queues_command_and_writes_minimal_audit_detail()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = HostUpgradeForm();

        var result = await fixture.InvokeRequestHostUpgradeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Upgrade%20request", location, StringComparison.Ordinal);
        var command = Assert.Single(await fixture.HostUpgrades.ListRecentAsync());
        Assert.Equal(HostUpgradeCommandPolicy.DefaultTarget, command.Target);
        Assert.Equal("hannah", command.RequestedBy);
        Assert.Equal(HostUpgradeCommandStatus.Pending, command.Status);

        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("HostUpgradeRequested", audit.Summary, StringComparison.Ordinal);
        Assert.Contains(command.Id.ToString(), audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"target\":\"vm\"", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hannah", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parameter", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Request_host_upgrade_accepts_freeform_new_target()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = HostUpgradeForm(newTarget: "mdaemon-mail01");

        var result = await fixture.InvokeRequestHostUpgradeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("mdaemon-mail01", Uri.UnescapeDataString(location), StringComparison.Ordinal);
        var command = Assert.Single(await fixture.HostUpgrades.ListRecentAsync());
        Assert.Equal("mdaemon-mail01", command.Target);

        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("\"target\":\"mdaemon-mail01\"", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Request_host_upgrade_rejects_invalid_target_charset()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = HostUpgradeForm(newTarget: "MDaemon Mail01");

        var result = await fixture.InvokeRequestHostUpgradeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Target%20must%20use", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.HostUpgrades.ListRecentAsync());
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Request_host_upgrade_surfaces_single_flight_rejection()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.HostUpgrades.RequestAsync(HostUpgradeCommandPolicy.DefaultTarget, "hannah");
        fixture.Context.Request.Form = HostUpgradeForm();

        var result = await fixture.InvokeRequestHostUpgradeAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("already%20pending%20or%20running", location, StringComparison.Ordinal);
        Assert.Single(await fixture.HostUpgrades.ListRecentAsync());
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Request_all_host_upgrades_requires_step_up()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        fixture.Context.Request.Form = EmptyForm();

        var result = await fixture.InvokeRequestAllHostUpgradesAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.HostUpgrades.ListRecentAsync());
    }

    [Fact]
    public async Task Request_all_host_upgrades_queues_every_known_target_and_audits_each()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.InstanceRegistry.UpsertAsync(Registration("mdaemon-MVCTMS01", "mvctms01"));
        await fixture.InstanceRegistry.UpsertAsync(Registration("mdaemon-MVCTMS02", "mvctms02"));
        fixture.Context.Request.Form = EmptyForm();

        var result = await fixture.InvokeRequestAllHostUpgradesAsync();
        var location = Uri.UnescapeDataString(await ExecuteRedirectAsync(result, fixture.Context));

        Assert.Contains("Queued 3 upgrade requests", location, StringComparison.Ordinal);
        Assert.Contains("vm", location, StringComparison.Ordinal);
        Assert.Contains("mvctms01", location, StringComparison.Ordinal);
        Assert.Contains("mvctms02", location, StringComparison.Ordinal);
        Assert.DoesNotContain("Skipped", location, StringComparison.Ordinal);

        var commands = await fixture.HostUpgrades.ListRecentAsync();
        Assert.Equal(3, commands.Count);
        Assert.Equal(
            ["mvctms01", "mvctms02", "vm"],
            commands.Select(c => c.Target).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        Assert.All(commands, c => Assert.Equal("hannah", c.RequestedBy));
        Assert.Equal(3, fixture.AuditLedger.Records.Count(r =>
            r.Summary.Contains("HostUpgradeRequested", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Request_all_host_upgrades_skips_in_flight_targets_and_reports_them()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.InstanceRegistry.UpsertAsync(Registration("mdaemon-MVCTMS01", "mvctms01"));
        await fixture.HostUpgrades.RequestAsync(HostUpgradeCommandPolicy.DefaultTarget, "hannah");
        fixture.Context.Request.Form = EmptyForm();

        var result = await fixture.InvokeRequestAllHostUpgradesAsync();
        var location = Uri.UnescapeDataString(await ExecuteRedirectAsync(result, fixture.Context));

        Assert.Contains("Queued 1 upgrade request", location, StringComparison.Ordinal);
        Assert.Contains("mvctms01", location, StringComparison.Ordinal);
        Assert.Contains("Skipped 1: vm (already pending or running)", location, StringComparison.Ordinal);

        var commands = await fixture.HostUpgrades.ListRecentAsync();
        Assert.Equal(2, commands.Count);
    }

    [Fact]
    public async Task Request_all_host_upgrades_with_everything_in_flight_reports_error()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.HostUpgrades.RequestAsync(HostUpgradeCommandPolicy.DefaultTarget, "hannah");
        fixture.Context.Request.Form = EmptyForm();

        var result = await fixture.InvokeRequestAllHostUpgradesAsync();
        var location = Uri.UnescapeDataString(await ExecuteRedirectAsync(result, fixture.Context));

        Assert.Contains("No upgrade requests were queued", location, StringComparison.Ordinal);
        Assert.Contains("Skipped 1: vm", location, StringComparison.Ordinal);
        Assert.Single(await fixture.HostUpgrades.ListRecentAsync());
    }

    private static InstanceRegistration Registration(string instanceId, string upgradeTarget) => new()
    {
        InstanceId = instanceId,
        Version = "test",
        Roles = "sources",
        UpgradeTarget = upgradeTarget,
        HostName = instanceId,
        StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
        ReportedAt = DateTimeOffset.UtcNow,
    };

    private static FormCollection EmptyForm() =>
        new(new Dictionary<string, StringValues>(StringComparer.Ordinal));

    [Fact]
    public async Task Save_ingestion_filters_rejects_locked_kind()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = IngestionForm(MDaemonEventKind.AuthenticationFailed);

        var result = await fixture.InvokeSaveIngestionFiltersAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("AuthenticationFailed", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.IngestionFilters.ListAsync());
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Save_ingestion_filters_ignores_unknown_kinds_and_audits_before_after_sets()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        await fixture.IngestionFilters.SeedDefaultsIfMissingAsync(fixture.Now);
        fixture.Context.Request.Form = IngestionForm(MDaemonEventKind.Other, unknown: "FutureKind");

        var result = await fixture.InvokeSaveIngestionFiltersAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Ingestion%20filters%20saved", location, StringComparison.Ordinal);
        var rows = await fixture.IngestionFilters.ListForSourceAsync(MDaemonIngestionFilterPolicy.SourceType);
        Assert.DoesNotContain(rows, filter => filter.EventKind == "FutureKind");
        Assert.True(rows.Single(filter => filter.EventKind == MDaemonEventKind.Other.ToString()).Suppressed);
        Assert.False(rows.Single(filter => filter.EventKind == MDaemonEventKind.SessionLine.ToString()).Suppressed);

        var audit = Assert.Single(fixture.AuditLedger.Records);
        Assert.Contains("changed ingestion filters", audit.Summary, StringComparison.Ordinal);
        Assert.Contains("IngestionFiltersChanged", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("beforeSuppressed", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("SessionLine", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("afterSuppressed", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("Other", audit.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FutureKind", audit.DetailJson, StringComparison.Ordinal);
    }

    private static async Task<string> ExecuteRedirectAsync(IResult result, HttpContext context)
    {
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        var location = context.Response.Headers.Location.ToString();

        // Banner messages travel in the protected flash cookie instead of the
        // query string.  Re-synthesize the legacy query form so assertions
        // keep verifying the exact user-visible message text.
        var flash = ReadFlashMessage(context);
        if (flash is null)
        {
            return location;
        }

        var key = flash.IsError ? "error" : "status";
        var fragmentStart = location.IndexOf('#', StringComparison.Ordinal);
        var basePath = fragmentStart < 0 ? location : location[..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : location[fragmentStart..];
        var separator = basePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{basePath}{separator}{key}={Uri.EscapeDataString(flash.Message)}{fragment}";
    }

    private static AdminFlashMessage? ReadFlashMessage(HttpContext context)
    {
        var setCookie = context.Response.Headers.SetCookie
            .FirstOrDefault(value => value?.StartsWith(AdminFlashMessages.CookieName + "=", StringComparison.Ordinal) == true);
        if (setCookie is null)
        {
            return null;
        }

        var reader = new DefaultHttpContext { RequestServices = context.RequestServices };
        reader.Request.Headers.Cookie = setCookie.Split(';')[0];
        return AdminFlashMessages.Consume(reader);
    }

    private static FormCollection Form(int version, params (RetentionTarget Target, int? Days)[] values)
    {
        var data = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var descriptor in RetentionTargetMetadata.Descriptors)
        {
            data[descriptor.SettingName] = string.Empty;
        }

        foreach (var (target, days) in values)
        {
            data[target.SettingName()] = days?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return new FormCollection(data);
    }

    private static FormCollection SatelliteForm(string name) => new(
        new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["name"] = name,
        });

    private static FormCollection JetPackForm(
        int version,
        string feedUrl = JetPackFeedSettings.DefaultFeedUrl,
        int fetchIntervalMinutes = 60,
        bool enabled = true,
        string addressListName = JetPackFeedSettings.DefaultAddressListName)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
            ["feedUrl"] = feedUrl,
            ["fetchIntervalMinutes"] = fetchIntervalMinutes.ToString(CultureInfo.InvariantCulture),
            ["addressListName"] = addressListName,
        };
        if (enabled)
        {
            values["enabled"] = "on";
        }

        return new FormCollection(values);
    }

    private static FormCollection RouterCreateForm(string name, string password) =>
        RouterForm(name, "http://router-a.example.com", MikroTikRouterTransportMode.PlainHttp, "viegard", password, null, enabled: true);

    private static FormCollection RouterUpdateForm(MikroTikRouter router, string password) =>
        RouterForm(
            router.Name,
            router.BaseUrl,
            router.TransportMode,
            router.Username,
            password,
            router.PinnedCertificateSha256,
            router.Enabled,
            router.Id,
            router.RowVersion);

    private static FormCollection RouterForm(
        string name,
        string baseUrl,
        MikroTikRouterTransportMode transportMode,
        string username,
        string password,
        string? pinnedCertificateSha256,
        bool enabled,
        Guid? id = null,
        int? rowVersion = null)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["baseUrl"] = baseUrl,
            ["transportMode"] = transportMode.ToString(),
            ["username"] = username,
            ["password"] = password,
            ["pinnedCertificateSha256"] = pinnedCertificateSha256 ?? string.Empty,
        };
        if (enabled)
        {
            values["enabled"] = "on";
        }

        if (id is { } routerId)
        {
            values["id"] = routerId.ToString("N");
        }

        if (rowVersion is { } version)
        {
            values["rowVersion"] = version.ToString(CultureInfo.InvariantCulture);
        }

        return new FormCollection(values);
    }

    private static FormCollection HostUpgradeForm(
        string target = HostUpgradeCommandPolicy.DefaultTarget,
        string? newTarget = null)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["target"] = target,
        };
        if (newTarget is not null)
        {
            values["newTarget"] = newTarget;
        }

        return new FormCollection(values);
    }

    private static FormCollection ThresholdForm(int rowVersion, double reviewConfidence, double actionConfidence, int severity) => new(
        new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["rowVersion"] = rowVersion.ToString(CultureInfo.InvariantCulture),
            ["reviewConfidence"] = reviewConfidence.ToString(CultureInfo.InvariantCulture),
            ["actionConfidence"] = actionConfidence.ToString(CultureInfo.InvariantCulture),
            ["actionMinSeverity"] = severity.ToString(CultureInfo.InvariantCulture),
        });

    private static FormCollection IngestionForm(params MDaemonEventKind[] suppressed) =>
        IngestionForm(suppressed, unknown: null);

    private static FormCollection IngestionForm(MDaemonEventKind[] suppressed, string? unknown)
    {
        var values = suppressed.Select(kind => kind.ToString()).ToList();
        if (!string.IsNullOrWhiteSpace(unknown))
        {
            values.Add(unknown);
        }

        return new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["suppressed"] = new StringValues(values.ToArray()),
        });
    }

    private static FormCollection IngestionForm(MDaemonEventKind suppressed, string? unknown) =>
        IngestionForm([suppressed], unknown);

    private static RetentionSettings SettingsWith(params (RetentionTarget Target, int? Days)[] values)
    {
        var settings = new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };
        foreach (var (target, days) in values)
        {
            settings = settings.WithDays(target, days);
        }

        return settings;
    }

    private static PolicyThresholdSettings ThresholdSettings() => new()
    {
        ReviewConfidence = 0.7,
        ActionConfidence = 0.9,
        ActionMinSeverity = 7,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };

    private static JetPackFeedSettings JetPackSettings(
        string feedUrl = JetPackFeedSettings.DefaultFeedUrl,
        TimeSpan? fetchInterval = null,
        bool enabled = true,
        string addressListName = JetPackFeedSettings.DefaultAddressListName) => new()
    {
        FeedUrl = feedUrl,
        FetchInterval = fetchInterval ?? JetPackFeedSettings.DefaultFetchInterval,
        Enabled = enabled,
        AddressListName = addressListName,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };

    private static PolicyPostureSettings PostureSettings(bool dryRun = true, bool manualApprovalMode = true, bool emergencyStop = false) => new()
    {
        DryRun = dryRun,
        ManualApprovalMode = manualApprovalMode,
        EmergencyStop = emergencyStop,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };

    private static FormCollection PostureForm(
        int rowVersion,
        bool dryRun,
        bool manualApprovalMode,
        bool emergencyStop,
        string? confirmEnforce = null)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["rowVersion"] = rowVersion.ToString(CultureInfo.InvariantCulture),
        };
        if (dryRun)
        {
            values["dryRun"] = "on";
        }

        if (manualApprovalMode)
        {
            values["manualApprovalMode"] = "on";
        }

        if (emergencyStop)
        {
            values["emergencyStop"] = "on";
        }

        if (confirmEnforce is not null)
        {
            values["confirmEnforce"] = confirmEnforce;
        }

        return new FormCollection(values);
    }

    private static MikroTikRouter Router(string name) => new()
    {
        Id = ViegardId.New(),
        Name = name,
        BaseUrl = $"http://{name}.example.com",
        TransportMode = MikroTikRouterTransportMode.PlainHttp,
        PinnedCertificateSha256 = null,
        Username = "viegard",
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
        RowVersion = 0,
    };

    private sealed record EndpointFixture(
        DefaultHttpContext Context,
        InMemoryRetentionSettingsStore RetentionSettings,
        InMemoryJetPackFeedSettingsStore JetPackSettings,
        InMemoryPolicyThresholdSettingsStore PolicyThresholds,
        InMemoryPolicyPostureSettingsStore PolicyPosture,
        RecordingAuditLedger AuditLedger,
        DateTimeOffset Now,
        NoopAntiforgery Antiforgery,
        InMemoryAdminUserStore Users,
        InMemoryAdminSessionStore Sessions,
        AdminAuthAuditor AuthAuditor,
        AdminConfigAuditor ConfigAuditor,
        InMemorySatelliteRoleStore SatelliteRoles,
        InMemoryMikroTikRouterStore Routers,
        IRouterCredentialProtector RouterProtector,
        InMemoryHostUpgradeCommandStore HostUpgrades,
        InMemoryInstanceRegistryStore InstanceRegistry,
        InMemoryIngestionFilterStore IngestionFilters,
        InMemoryAdminErrorStore AdminErrors,
        SatelliteRoleCredentialCookie SatelliteCredentialCookie)
    {
        public static async Task<EndpointFixture> CreateAsync(bool freshStepUp)
        {
            var now = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);
            var user = new AdminUser
            {
                Id = ViegardId.New(),
                Username = "hannah",
                PasswordHash = "hash",
                PasswordChangedAt = now,
                FailedLoginCount = 0,
                LockedUntil = null,
                MustChangePassword = false,
                TotpEnrolled = true,
                CreatedAt = now,
            };
            var session = new AdminSession
            {
                Id = ViegardId.New(),
                UserId = user.Id,
                CreatedAt = now,
                LastSeenAt = now,
                AbsoluteExpiresAt = now.AddDays(1),
                IdleExpiresAt = now.AddHours(1),
                Ip = "127.0.0.1",
                IpBindingMode = AdminIpBindingModes.Strict,
                UserAgent = "test",
                RevokedAt = null,
                StepUpAt = freshStepUp ? DateTimeOffset.UtcNow : null,
            };

            var users = new InMemoryAdminUserStore();
            var sessions = new InMemoryAdminSessionStore();
            await users.CreateAsync(user);
            await sessions.CreateAsync(session);

            var auditLedger = new RecordingAuditLedger();
            var authAuditor = new AdminAuthAuditor(
                auditLedger,
                new InMemoryRawObservationStore(),
                new InMemoryEventStore(),
                new ChannelWorkQueue<Guid>("admin-test"),
                NullLogger<AdminAuthAuditor>.Instance);
            var configAuditor = new AdminConfigAuditor(auditLedger, NullLogger<AdminConfigAuditor>.Instance);
            var satelliteRoles = new InMemorySatelliteRoleStore();
            var routers = new InMemoryMikroTikRouterStore();
            var routerProtector = new PlainRouterCredentialProtector();
            var hostUpgrades = new InMemoryHostUpgradeCommandStore();
            var ingestionFilters = new InMemoryIngestionFilterStore();
            var jetPackSettings = new InMemoryJetPackFeedSettingsStore();
            var policyThresholds = new InMemoryPolicyThresholdSettingsStore();
            var policyPosture = new InMemoryPolicyPostureSettingsStore();
            var satelliteCredentialCookie = new SatelliteRoleCredentialCookie(new NoopDataProtectionProvider());
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IOptions<AdminAuthOptions>>(Options.Create(new AdminAuthOptions()))
                .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
                .BuildServiceProvider();

            var context = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(AdminCookieNames.SessionIdClaim, session.Id.ToString()),
                ], "test")),
            };
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/x-www-form-urlencoded";

            return new EndpointFixture(
                context,
                new InMemoryRetentionSettingsStore(),
                jetPackSettings,
                policyThresholds,
                policyPosture,
                auditLedger,
                now,
                new NoopAntiforgery(),
                users,
                sessions,
                authAuditor,
                configAuditor,
                satelliteRoles,
                routers,
                routerProtector,
                hostUpgrades,
                new InMemoryInstanceRegistryStore(),
                ingestionFilters,
                new InMemoryAdminErrorStore(),
                satelliteCredentialCookie);
        }

        public Task<IResult> InvokeAsync() =>
            AdminConfigurationEndpoints.SaveRetentionAsync(
                Context,
                Antiforgery,
                RetentionSettings,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeSaveJetPackAsync() =>
            AdminConfigurationEndpoints.SaveJetPackAsync(
                Context,
                Antiforgery,
                JetPackSettings,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeCreateSatelliteAsync() =>
            AdminConfigurationEndpoints.CreateSatelliteAsync(
                Context,
                Antiforgery,
                SatelliteRoles,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor,
                SatelliteCredentialCookie);

        public Task<IResult> InvokeCreateRouterAsync() =>
            AdminConfigurationEndpoints.CreateRouterAsync(
                Context,
                Antiforgery,
                Routers,
                RouterProtector,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeUpdateRouterAsync() =>
            AdminConfigurationEndpoints.UpdateRouterAsync(
                Context,
                Antiforgery,
                Routers,
                RouterProtector,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeRequestHostUpgradeAsync() =>
            AdminConfigurationEndpoints.RequestHostUpgradeAsync(
                Context,
                Antiforgery,
                HostUpgrades,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeRequestAllHostUpgradesAsync() =>
            AdminConfigurationEndpoints.RequestAllHostUpgradesAsync(
                Context,
                Antiforgery,
                HostUpgrades,
                InstanceRegistry,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeSavePolicyThresholdsAsync() =>
            AdminConfigurationEndpoints.SavePolicyThresholdsAsync(
                Context,
                Antiforgery,
                PolicyThresholds,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeSavePolicyPostureAsync() =>
            AdminConfigurationEndpoints.SavePolicyPostureAsync(
                Context,
                Antiforgery,
                PolicyPosture,
                Options.Create(new PolicyOptions()),
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeSaveIngestionFiltersAsync() =>
            AdminConfigurationEndpoints.SaveIngestionFiltersAsync(
                Context,
                Antiforgery,
                IngestionFilters,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeClearErrorsAsync() =>
            Viegard.AdminApi.Errors.AdminErrorEndpoints.ClearAsync(
                Context,
                Antiforgery,
                AdminErrors,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);
    }

    private sealed class RecordingAuditLedger : IAuditLedger
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<KeysetPage<AuditRecord>> ListPageAsync(
            Guid? beforeId,
            int pageSize,
            AuditListFilter? filter = null,
            ListSort<AuditSortColumn>? sort = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KeysetPage<AuditRecord>(Records, null, Records.Count, 0));

        public ValueTask<Guid?> GetPageCursorAsync(
            int pageNumber,
            int pageSize,
            AuditListFilter? filter = null,
            ListSort<AuditSortColumn>? sort = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Guid?>(null);
    }

    private sealed class NoopAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            Task.CompletedTask;
    }

    private sealed class NoopDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new NoopDataProtector();
    }

    private sealed class PlainRouterCredentialProtector : IRouterCredentialProtector
    {
        public string Protect(Guid routerId, string password) =>
            $"protected:{routerId:N}:{password}";

        public string Unprotect(Guid routerId, string ciphertext)
        {
            var prefix = $"protected:{routerId:N}:";
            if (!ciphertext.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new RouterCredentialProtectionException("Router credential ciphertext could not be authenticated for this router.");
            }

            return ciphertext[prefix.Length..];
        }
    }

    private sealed class NoopDataProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }
}
