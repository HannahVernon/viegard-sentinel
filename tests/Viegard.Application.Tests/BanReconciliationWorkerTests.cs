using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Viegard.Actions.MikroTik;
using Viegard.Application.Policy;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class BanReconciliationWorkerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Reconciler_adds_missing_entries_with_remaining_time()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        var decisionId = Guid.Parse("018f6ad8-98e8-7b71-a62c-2f41829f2e41");
        await fixture.ActiveBanRows.UpsertByIpAsync(CreateActiveBan(fixture, "203.0.113.10", TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(30)), decisionId));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "[]"));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.Created, "{}"));

        var result = await Worker(fixture, audit).RunCycleAsync();

        Assert.True(result.Changed);
        Assert.Equal(["GET", "PUT"], fixture.Http.Requests.Select(request => request.Method).ToArray());
        using var body = JsonDocument.Parse(fixture.Http.Requests[1].Body!);
        Assert.Equal("viegard-banned", body.RootElement.GetProperty("list").GetString());
        Assert.Equal("203.0.113.10", body.RootElement.GetProperty("address").GetString());
        Assert.Equal("5m30s", body.RootElement.GetProperty("timeout").GetString());
        Assert.Equal($"viegard reconciled {decisionId:N}", body.RootElement.GetProperty("comment").GetString());
        Assert.Equal("203.0.113.10", Assert.Single(Assert.Single(result.Routers).Added));
        var auditRecord = Assert.Single(audit.Snapshot());
        Assert.Equal(PipelineStage.Action, auditRecord.Stage);
    }

    [Fact]
    public async Task Reconciler_removes_extraneous_entries()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, """[{".id":"*1","address":"198.51.100.7"}]"""));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.NoContent, string.Empty));

        var result = await Worker(fixture, audit).RunCycleAsync();

        Assert.True(result.Changed);
        Assert.Equal(["GET", "DELETE"], fixture.Http.Requests.Select(request => request.Method).ToArray());
        Assert.EndsWith("/%2A1", fixture.Http.Requests[1].Url, StringComparison.Ordinal);
        Assert.Equal("198.51.100.7", Assert.Single(Assert.Single(result.Routers).Removed));
        Assert.Single(audit.Snapshot());
    }

    [Fact]
    public async Task Reconciler_treats_unparseable_addresses_as_extraneous()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, """[{".id":"*bad","address":"manual-entry"}]"""));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "{}"));

        var result = await Worker(fixture, audit).RunCycleAsync();

        Assert.Equal(["GET", "DELETE"], fixture.Http.Requests.Select(request => request.Method).ToArray());
        Assert.EndsWith("/%2Abad", fixture.Http.Requests[1].Url, StringComparison.Ordinal);
        Assert.Equal("manual-entry", Assert.Single(Assert.Single(result.Routers).Removed));
    }

    [Fact]
    public async Task Reconciler_skips_router_when_list_read_is_truncated()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        await fixture.ActiveBanRows.UpsertByIpAsync(CreateActiveBan(fixture, "203.0.113.10", TimeSpan.FromMinutes(5)));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, new string(' ', 256 * 1024 + 1)));

        var result = await Worker(fixture, audit).RunCycleAsync();

        Assert.False(result.Changed);
        Assert.Single(fixture.Http.Requests);
        var routerResult = Assert.Single(result.Routers);
        Assert.True(routerResult.Skipped);
        Assert.Contains("exceeded", routerResult.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(audit.Snapshot());
    }

    [Fact]
    public async Task Reconciler_dry_run_reads_and_reports_drift_without_mutating()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        await fixture.ActiveBanRows.UpsertByIpAsync(CreateActiveBan(fixture, "203.0.113.10", TimeSpan.FromMinutes(5)));
        await fixture.ActiveBanRows.UpsertByIpAsync(CreateActiveBan(fixture, "203.0.113.99", -TimeSpan.FromMinutes(1)));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, """[{".id":"*1","address":"198.51.100.7"}]"""));

        var result = await Worker(fixture, audit, dryRun: true).RunCycleAsync();

        Assert.False(result.Changed);
        Assert.Single(fixture.Http.Requests);
        var routerResult = Assert.Single(result.Routers);
        Assert.Equal("203.0.113.10", Assert.Single(routerResult.Added));
        Assert.Equal("198.51.100.7", Assert.Single(routerResult.Removed));
        Assert.NotNull(await fixture.ActiveBanRows.GetByIpAsync("203.0.113.99"));
        Assert.Empty(audit.Snapshot());
    }

    [Fact]
    public async Task Reconciler_emergency_stop_skips_without_reads_or_writes()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        await fixture.AddRouterAsync("router-a");
        await fixture.ActiveBanRows.UpsertByIpAsync(CreateActiveBan(fixture, "203.0.113.10", -TimeSpan.FromMinutes(1)));

        var result = await Worker(fixture, audit, emergencyStop: true).RunCycleAsync();

        Assert.True(result.EmergencyStop);
        Assert.Empty(fixture.Http.Requests);
        Assert.NotNull(await fixture.ActiveBanRows.GetByIpAsync("203.0.113.10"));
        Assert.Empty(audit.Snapshot());
    }

    [Fact]
    public async Task Reconciler_does_not_audit_unchanged_cycles()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        await fixture.ActiveBanRows.UpsertByIpAsync(CreateActiveBan(fixture, "203.0.113.10", TimeSpan.FromMinutes(5)));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, """[{".id":"*1","address":"203.0.113.10"}]"""));

        var result = await Worker(fixture, audit).RunCycleAsync();

        Assert.False(result.Changed);
        Assert.Single(fixture.Http.Requests);
        Assert.Empty(audit.Snapshot());
    }

    [Fact]
    public async Task Reconciler_does_not_reapply_near_expiry_entries()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        await fixture.ActiveBanRows.UpsertByIpAsync(CreateActiveBan(fixture, "203.0.113.10", TimeSpan.FromSeconds(60)));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "[]"));

        var result = await Worker(fixture, audit).RunCycleAsync();

        Assert.False(result.Changed);
        Assert.Single(fixture.Http.Requests);
        Assert.Empty(Assert.Single(result.Routers).Added);
        Assert.Empty(audit.Snapshot());
    }

    private static BanReconciliationWorker Worker(
        ProviderFixture fixture,
        InMemoryAuditLedger audit,
        bool dryRun = false,
        bool emergencyStop = false) =>
        new(
            fixture.ActiveBans,
            fixture.Routers,
            fixture.Credentials,
            Options.Create(new PolicyOptions
            {
                Posture = new PolicyPostureOptions
                {
                    DryRun = dryRun,
                    EmergencyStop = emergencyStop,
                    ManualApprovalMode = true,
                },
            }),
            Options.Create(new ActionWorkerOptions()),
            fixture.Http,
            audit,
            fixture.Time,
            NullLogger<BanReconciliationWorker>.Instance);

    private static ActiveBan CreateActiveBan(ProviderFixture fixture, string ip, TimeSpan ttl, Guid? decisionId = null) =>
        CreateActiveBan(fixture, ip, fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow().Add(ttl), decisionId);

    private static ActiveBan CreateActiveBan(
        ProviderFixture fixture,
        string ip,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        Guid? decisionId = null) => new()
        {
            Id = ViegardId.New(),
            Ip = ip,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            DecisionId = decisionId ?? ViegardId.New(),
            ActionId = ViegardId.New(),
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
