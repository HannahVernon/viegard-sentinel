using Viegard.Application.Coalescing;
using Viegard.Application.Correlation;
using Viegard.Application.Detection;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class IncidentCoalescingCorrelatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static TimeWindowCorrelator CoalescingCorrelator(
        InMemoryIncidentStore store,
        int settleWindowSeconds = 10,
        int maxCoalesceWindowSeconds = 300) =>
        new(
            [new UriContainsRule("hostile")],
            store,
            new CorrelationOptions(),
            new IncidentCoalescingSettingsSource(),
            new IncidentCoalescingOptions
            {
                Enabled = true,
                SettleWindowSeconds = settleWindowSeconds,
                MaxCoalesceWindowSeconds = maxCoalesceWindowSeconds,
            });

    [Fact]
    public async Task Created_incident_sets_coalesce_until_to_settle_window()
    {
        var store = new InMemoryIncidentStore();
        var correlator = CoalescingCorrelator(store, settleWindowSeconds: 10);

        var incident = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.10", Start)));

        Assert.Equal(Start.AddSeconds(10), incident.CoalesceUntil);
    }

    [Fact]
    public async Task Event_inside_window_absorbs_and_extends_coalesce_until()
    {
        var store = new InMemoryIncidentStore();
        var correlator = CoalescingCorrelator(store, settleWindowSeconds: 10);

        var first = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.11", Start)));
        var second = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/two", "198.51.100.11", Start.AddSeconds(5))));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.EventIds.Count);
        Assert.Equal(Start.AddSeconds(15), second.CoalesceUntil);
    }

    [Fact]
    public async Task Event_after_window_starts_a_new_incident()
    {
        var store = new InMemoryIncidentStore();
        var correlator = CoalescingCorrelator(store, settleWindowSeconds: 10);

        var first = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.12", Start)));
        var second = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/two", "198.51.100.12", Start.AddSeconds(20))));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Single(second.EventIds);
    }

    [Fact]
    public async Task Coalesce_until_is_capped_by_max_window()
    {
        var store = new InMemoryIncidentStore();
        var correlator = CoalescingCorrelator(store, settleWindowSeconds: 10, maxCoalesceWindowSeconds: 12);

        await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.13", Start));
        var second = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/two", "198.51.100.13", Start.AddSeconds(8))));

        // 8 + 10 = 18 would exceed the 12s cap from window start.
        Assert.Equal(Start.AddSeconds(12), second.CoalesceUntil);
    }

    [Fact]
    public async Task Finalized_incident_with_null_window_is_not_absorbable()
    {
        var store = new InMemoryIncidentStore();
        var correlator = CoalescingCorrelator(store, settleWindowSeconds: 10);

        var first = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.14", Start)));
        await store.UpsertAsync(first with { CoalesceUntil = null });

        var second = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/two", "198.51.100.14", Start.AddSeconds(3))));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Disabled_source_leaves_coalesce_until_null()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator([new UriContainsRule("hostile")], store, new CorrelationOptions());

        var incident = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.15", Start)));

        Assert.Null(incident.CoalesceUntil);
    }

    private static NormalizedEvent HttpEvent(string uri, string remoteAddress, DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            SourceId = "nginx-test",
            SourceType = "nginx",
            OccurredAt = occurredAt,
            Entities = [new EntityRef(EntityKind.IpAddress, remoteAddress)],
            Payload = new HttpRequestEvent
            {
                RemoteAddress = remoteAddress,
                Method = "GET",
                Uri = uri,
                Protocol = "HTTP/1.1",
                StatusCode = 200,
                UserAgent = "Mozilla/5.0",
            },
            RawObservationId = Guid.NewGuid(),
        };

    private sealed class UriContainsRule(string marker) : IDetectionRule
    {
        public string RuleId => "test.uri-contains";

        public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e) =>
            e.Payload is HttpRequestEvent http
                && http.Uri is not null
                && http.Uri.Contains(marker, StringComparison.OrdinalIgnoreCase)
                ? [new EvidenceItem { Description = "test evidence", Score = 0.5, EventId = e.Id }]
                : [];
    }
}
