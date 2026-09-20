using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class JetPackFeedFetchWorkerTests
{
    [Fact]
    public async Task Fetch_worker_applies_flat_array_payload()
    {
        var settings = new InMemoryJetPackFeedSettingsStore();
        var desired = new InMemoryJetPackDesiredAddressStore();
        var now = new ManualTimeProvider(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
        await settings.UpsertAsync(Settings(), 0, "hannah", now.GetUtcNow());
        var worker = Worker(settings, desired, now, """["122.248.245.244/32","192.0.80.0/20"]""");

        var result = await worker.RunCycleAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Added);
        Assert.Equal(["122.248.245.244/32", "192.0.80.0/20"], (await desired.ListAsync()).Select(row => row.Address).ToArray());
    }

    [Fact]
    public async Task Fetch_worker_accepts_object_payload_with_array_property()
    {
        var settings = new InMemoryJetPackFeedSettingsStore();
        var desired = new InMemoryJetPackDesiredAddressStore();
        var now = new ManualTimeProvider(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
        await settings.UpsertAsync(Settings(), 0, "hannah", now.GetUtcNow());
        var worker = Worker(settings, desired, now, """{"ips":["122.248.245.244/32","192.0.80.0/20"]}""");

        var result = await worker.RunCycleAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.CurrentCount);
    }

    [Fact]
    public async Task Fetch_worker_does_not_clear_existing_desired_state_when_payload_is_invalid()
    {
        var settings = new InMemoryJetPackFeedSettingsStore();
        var desired = new InMemoryJetPackDesiredAddressStore();
        var now = new ManualTimeProvider(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
        await settings.UpsertAsync(Settings(), 0, "hannah", now.GetUtcNow());
        await desired.ReplaceSnapshotAsync(["192.0.80.0/20"], now.GetUtcNow());
        var worker = Worker(settings, desired, now, """{"ips":["bad-value"]}""");

        var result = await worker.RunCycleAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(["192.0.80.0/20"], (await desired.ListAsync()).Select(row => row.Address).ToArray());
    }

    [Fact]
    public async Task Fetch_worker_skips_when_disabled()
    {
        var settings = new InMemoryJetPackFeedSettingsStore();
        var desired = new InMemoryJetPackDesiredAddressStore();
        var now = new ManualTimeProvider(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
        await settings.UpsertAsync(Settings(enabled: false), 0, "hannah", now.GetUtcNow());
        var worker = Worker(settings, desired, now, """["122.248.245.244/32"]""");

        var result = await worker.RunCycleAsync();

        Assert.True(result.Skipped);
        Assert.Empty(await desired.ListAsync());
    }

    private static JetPackFeedFetchWorker Worker(
        IJetPackFeedSettingsStore settings,
        IJetPackDesiredAddressStore desired,
        ManualTimeProvider now,
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(
            settings,
            desired,
            new HttpClient(new StubHandler(body, statusCode), disposeHandler: true),
            Options.Create(new JetPackFeedOptions()),
            now,
            NullLogger<JetPackFeedFetchWorker>.Instance);

    private static JetPackFeedSettings Settings(
        string feedUrl = JetPackFeedSettings.DefaultFeedUrl,
        bool enabled = true,
        TimeSpan? fetchInterval = null,
        string addressListName = JetPackFeedSettings.DefaultAddressListName) => new()
    {
        FeedUrl = feedUrl,
        Enabled = enabled,
        FetchInterval = fetchInterval ?? JetPackFeedSettings.DefaultFetchInterval,
        AddressListName = addressListName,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };

    private sealed class StubHandler(string body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            });
    }
}
