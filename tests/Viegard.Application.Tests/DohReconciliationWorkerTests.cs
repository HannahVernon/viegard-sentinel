using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;
using Viegard.Application.Doh;
using Viegard.Application.Policy;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class DohReconciliationWorkerTests
{
    private const string ListName = DohBlocklistSettings.DefaultAddressListName;

    [Fact]
    public async Task Reconciler_adds_missing_entries_with_feed_comment()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: true, applyToRouters: true);
        var desired = new InMemoryDohDesiredAddressStore();
        await desired.ReplaceSnapshotAsync(["1.1.1.1"], fixture.Time.GetUtcNow());
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "[]"));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.Created, "{}"));

        var result = await Worker(fixture, settings, desired, new InMemoryDohProbeResultStore(), audit).RunCycleAsync();

        Assert.True(result.Changed);
        Assert.Equal(["GET", "PUT"], fixture.Http.Requests.Select(request => request.Method).ToArray());
        using var body = System.Text.Json.JsonDocument.Parse(fixture.Http.Requests[1].Body!);
        Assert.Equal(ListName, body.RootElement.GetProperty("list").GetString());
        Assert.Equal("1.1.1.1", body.RootElement.GetProperty("address").GetString());
        Assert.StartsWith("doh-feed", body.RootElement.GetProperty("comment").GetString(), StringComparison.Ordinal);
        Assert.Equal("1.1.1.1", Assert.Single(Assert.Single(result.Routers).Added));
        Assert.Single(audit.Snapshot());
    }

    [Fact]
    public async Task Reconciler_annotates_confirmed_addresses()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: true, applyToRouters: true);
        var desired = new InMemoryDohDesiredAddressStore();
        await desired.ReplaceSnapshotAsync(["1.1.1.1"], fixture.Time.GetUtcNow());
        var probes = new InMemoryDohProbeResultStore();
        await probes.SaveAsync(new DohProbeResult
        {
            Address = "1.1.1.1",
            Status = DohProbeStatus.Confirmed,
            HttpStatus = 200,
            TokenMatched = true,
            LastProbedAt = fixture.Time.GetUtcNow(),
        });
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "[]"));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.Created, "{}"));

        await Worker(fixture, settings, desired, probes, audit).RunCycleAsync();

        using var body = System.Text.Json.JsonDocument.Parse(fixture.Http.Requests[1].Body!);
        Assert.StartsWith("doh-confirmed", body.RootElement.GetProperty("comment").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconciler_removes_extraneous_entries()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: true, applyToRouters: true);
        var desired = new InMemoryDohDesiredAddressStore();
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, $$"""[{".id":"*1","list":"{{ListName}}","address":"203.0.113.9"}]"""));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.NoContent, string.Empty));

        var result = await Worker(fixture, settings, desired, new InMemoryDohProbeResultStore(), audit).RunCycleAsync();

        Assert.True(result.Changed);
        Assert.Equal(["GET", "DELETE"], fixture.Http.Requests.Select(request => request.Method).ToArray());
        Assert.EndsWith("/*1", fixture.Http.Requests[1].Url, StringComparison.Ordinal);
        Assert.Equal("203.0.113.9", Assert.Single(Assert.Single(result.Routers).Removed));
    }

    [Fact]
    public async Task Reconciler_dry_run_reports_drift_without_mutating()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: true, applyToRouters: false);
        var desired = new InMemoryDohDesiredAddressStore();
        await desired.ReplaceSnapshotAsync(["1.1.1.1"], fixture.Time.GetUtcNow());
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, $$"""[{".id":"*1","list":"{{ListName}}","address":"203.0.113.9"}]"""));
        var proposals = new InMemoryDohReconciliationProposalStore();

        var result = await Worker(fixture, settings, desired, new InMemoryDohProbeResultStore(), audit, proposals).RunCycleAsync();

        Assert.False(result.Changed);
        Assert.True(result.HasChanges);
        Assert.Single(fixture.Http.Requests);
        Assert.Equal("GET", fixture.Http.Requests[0].Method);
        var routerResult = Assert.Single(result.Routers);
        Assert.Equal("1.1.1.1", Assert.Single(routerResult.Added));
        Assert.Equal("203.0.113.9", Assert.Single(routerResult.Removed));

        // Propose-only cycles now record an audit proposal and persist the snapshot.
        var record = Assert.Single(audit.Snapshot());
        Assert.Contains("propose-only", record.Summary, StringComparison.Ordinal);
        var proposal = await proposals.GetAsync();
        Assert.NotNull(proposal);
        Assert.True(proposal!.DryRun);
        Assert.Equal(1, proposal.TotalAdd);
        Assert.Equal(1, proposal.TotalRemove);
        var proposalRouter = Assert.Single(proposal.Routers);
        Assert.Equal("1.1.1.1", Assert.Single(proposalRouter.ToAdd));
        Assert.Equal("203.0.113.9", Assert.Single(proposalRouter.ToRemove));
    }

    [Fact]
    public async Task Reconciler_skips_when_disabled()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: false, applyToRouters: true);
        var desired = new InMemoryDohDesiredAddressStore();

        var result = await Worker(fixture, settings, desired, new InMemoryDohProbeResultStore(), audit).RunCycleAsync();

        Assert.True(result.Disabled);
        Assert.Empty(fixture.Http.Requests);
        Assert.Empty(audit.Snapshot());
    }

    private static DohReconciliationWorker Worker(
        ProviderFixture fixture,
        IDohBlocklistSettingsStore settings,
        IDohDesiredAddressStore desired,
        IDohProbeResultStore probes,
        InMemoryAuditLedger audit,
        IDohReconciliationProposalStore? proposals = null) =>
        new(
            settings,
            desired,
            probes,
            proposals ?? new InMemoryDohReconciliationProposalStore(),
            fixture.Routers,
            fixture.Credentials,
            Options.Create(new ActionWorkerOptions()),
            Options.Create(new DohBlocklistOptions()),
            Options.Create(new PolicyOptions
            {
                Posture = new PolicyPostureOptions
                {
                    DryRun = false,
                    EmergencyStop = false,
                    ManualApprovalMode = true,
                },
            }),
            new PolicyPostureSource(),
            fixture.Http,
            audit,
            fixture.Time,
            NullLogger<DohReconciliationWorker>.Instance);

    private static async Task<IDohBlocklistSettingsStore> SeedSettingsAsync(bool enabled, bool applyToRouters)
    {
        var store = new InMemoryDohBlocklistSettingsStore();
        await store.UpdateAsync(
            new DohBlocklistSettings { Enabled = enabled, ApplyToRouters = applyToRouters },
            expectedRowVersion: 0,
            updatedBy: "test",
            updatedAt: DateTimeOffset.UtcNow);
        return store;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
