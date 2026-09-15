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
using Viegard.Application.Queues;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Domain.Events;
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
        return context.Response.Headers.Location.ToString();
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

    private static FormCollection HostUpgradeForm() => new(
        new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["target"] = HostUpgradeCommandPolicy.DefaultTarget,
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

    private sealed record EndpointFixture(
        DefaultHttpContext Context,
        InMemoryRetentionSettingsStore RetentionSettings,
        RecordingAuditLedger AuditLedger,
        DateTimeOffset Now,
        NoopAntiforgery Antiforgery,
        InMemoryAdminUserStore Users,
        InMemoryAdminSessionStore Sessions,
        AdminAuthAuditor AuthAuditor,
        AdminConfigAuditor ConfigAuditor,
        InMemorySatelliteRoleStore SatelliteRoles,
        InMemoryHostUpgradeCommandStore HostUpgrades,
        InMemoryIngestionFilterStore IngestionFilters,
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
            var hostUpgrades = new InMemoryHostUpgradeCommandStore();
            var ingestionFilters = new InMemoryIngestionFilterStore();
            var satelliteCredentialCookie = new SatelliteRoleCredentialCookie(new NoopDataProtectionProvider());
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IOptions<AdminAuthOptions>>(Options.Create(new AdminAuthOptions()))
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
                auditLedger,
                now,
                new NoopAntiforgery(),
                users,
                sessions,
                authAuditor,
                configAuditor,
                satelliteRoles,
                hostUpgrades,
                ingestionFilters,
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

        public Task<IResult> InvokeRequestHostUpgradeAsync() =>
            AdminConfigurationEndpoints.RequestHostUpgradeAsync(
                Context,
                Antiforgery,
                HostUpgrades,
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

    private sealed class NoopDataProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }
}
