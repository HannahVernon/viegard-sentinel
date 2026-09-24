using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Viegard.Application.Doh;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class DohFeedFetchWorkerTests
{
    [Fact]
    public async Task Fetch_worker_applies_merged_snapshot()
    {
        var settings = new InMemoryDohBlocklistSettingsStore();
        var desired = new InMemoryDohDesiredAddressStore();
        var now = new ManualTimeProvider(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        await settings.UpdateAsync(new DohBlocklistSettings(), 0, "hannah", now.GetUtcNow());
        var worker = Worker(settings, desired, now, "1.1.1.1\n8.8.8.8\n# comment\n2606:4700::1111\n");

        var result = await worker.RunCycleAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Added);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], (await desired.ListAsync()).Select(row => row.Address).ToArray());
    }

    [Fact]
    public async Task Fetch_worker_skips_when_disabled()
    {
        var settings = new InMemoryDohBlocklistSettingsStore();
        var desired = new InMemoryDohDesiredAddressStore();
        var now = new ManualTimeProvider(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        await settings.UpdateAsync(new DohBlocklistSettings { Enabled = false }, 0, "hannah", now.GetUtcNow());
        var worker = Worker(settings, desired, now, "1.1.1.1\n");

        var result = await worker.RunCycleAsync();

        Assert.True(result.Skipped);
        Assert.Empty(await desired.ListAsync());
    }

    [Fact]
    public async Task Fetch_worker_preserves_snapshot_when_feed_fails()
    {
        var settings = new InMemoryDohBlocklistSettingsStore();
        var desired = new InMemoryDohDesiredAddressStore();
        var now = new ManualTimeProvider(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        await settings.UpdateAsync(new DohBlocklistSettings(), 0, "hannah", now.GetUtcNow());
        await desired.ReplaceSnapshotAsync(["1.1.1.1"], now.GetUtcNow());
        var worker = Worker(settings, desired, now, "unavailable", HttpStatusCode.ServiceUnavailable);

        var result = await worker.RunCycleAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(["1.1.1.1"], (await desired.ListAsync()).Select(row => row.Address).ToArray());
    }

    private static DohFeedFetchWorker Worker(
        IDohBlocklistSettingsStore settings,
        IDohDesiredAddressStore desired,
        ManualTimeProvider now,
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(
            settings,
            desired,
            new HttpClient(new StubFeedHandler(body, statusCode), disposeHandler: true),
            Options.Create(new DohBlocklistOptions()),
            now,
            NullLogger<DohFeedFetchWorker>.Instance);

    private sealed class StubFeedHandler(string body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            });
    }
}
