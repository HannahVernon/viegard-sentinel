using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;
using Viegard.Domain.Audit;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class JetPackReconciliationWorkerTests
{
    [Fact]
    public async Task Reconciler_adds_missing_entries_without_timeout()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: true);
        var desired = new InMemoryJetPackDesiredAddressStore();
        await desired.ReplaceSnapshotAsync(["122.248.245.244/32"], fixture.Time.GetUtcNow());
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "[]"));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.Created, "{}"));

        var result = await Worker(fixture, settings, desired, audit).RunCycleAsync();

        Assert.True(result.Changed);
        Assert.Equal(["GET", "PUT"], fixture.Http.Requests.Select(request => request.Method).ToArray());
        using var body = System.Text.Json.JsonDocument.Parse(fixture.Http.Requests[1].Body!);
        Assert.Equal("jetpack_servers", body.RootElement.GetProperty("list").GetString());
        Assert.Equal("122.248.245.244/32", body.RootElement.GetProperty("address").GetString());
        Assert.False(body.RootElement.TryGetProperty("timeout", out _));
        Assert.Equal("122.248.245.244/32", Assert.Single(Assert.Single(result.Routers).Added));
        Assert.Single(audit.Snapshot());
    }

    [Fact]
    public async Task Reconciler_removes_extraneous_entries_from_owned_list_only()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        var router = await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: true);
        var desired = new InMemoryJetPackDesiredAddressStore();
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, """[{".id":"*1","list":"jetpack_servers","address":"198.51.100.7"},{".id":"*2","list":"corp-allowlist","address":"203.0.113.10"}]"""));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.NoContent, string.Empty));

        var result = await Worker(fixture, settings, desired, audit).RunCycleAsync();

        Assert.True(result.Changed);
        Assert.Equal(["GET", "DELETE"], fixture.Http.Requests.Select(request => request.Method).ToArray());
        Assert.EndsWith("/*1", fixture.Http.Requests[1].Url, StringComparison.Ordinal);
        Assert.Equal("198.51.100.7", Assert.Single(Assert.Single(result.Routers).Removed));
    }

    [Fact]
    public async Task Reconciler_skips_when_settings_are_disabled()
    {
        var fixture = new ProviderFixture();
        var audit = new InMemoryAuditLedger();
        await fixture.AddRouterAsync("router-a");
        var settings = await SeedSettingsAsync(enabled: false);
        var desired = new InMemoryJetPackDesiredAddressStore();

        var result = await Worker(fixture, settings, desired, audit).RunCycleAsync();

        Assert.True(result.Disabled);
        Assert.Empty(fixture.Http.Requests);
        Assert.Empty(audit.Snapshot());
    }

    private static JetPackReconciliationWorker Worker(
        ProviderFixture fixture,
        IJetPackFeedSettingsStore settings,
        IJetPackDesiredAddressStore desired,
        InMemoryAuditLedger audit) =>
        new(
            settings,
            desired,
            fixture.Routers,
            fixture.Credentials,
            Options.Create(new ActionWorkerOptions()),
            Options.Create(new JetPackFeedOptions()),
            fixture.Http,
            audit,
            fixture.Time,
            NullLogger<JetPackReconciliationWorker>.Instance);

    private static async Task<IJetPackFeedSettingsStore> SeedSettingsAsync(bool enabled)
    {
        var store = new InMemoryJetPackFeedSettingsStore();
        await store.UpsertAsync(
            new JetPackFeedSettings
            {
                FeedUrl = JetPackFeedSettings.DefaultFeedUrl,
                FetchInterval = JetPackFeedSettings.DefaultFetchInterval,
                Enabled = enabled,
                AddressListName = JetPackFeedSettings.DefaultAddressListName,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "test",
            },
            expectedVersion: 0,
            updatedBy: "test",
            updatedAt: DateTimeOffset.UtcNow);
        return store;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
