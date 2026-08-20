using Viegard.Application.Correlation;
using Viegard.Application.Detection;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class TimeWindowCorrelatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scored_event_creates_open_incident()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator([new UriContainsRule("hostile")], store, new CorrelationOptions());
        var normalizedEvent = HttpEvent("/hostile/.env", "198.51.100.1", Start);

        var incidents = await correlator.CorrelateAsync(normalizedEvent);

        var incident = Assert.Single(incidents);
        Assert.Equal("ip=198.51.100.1", incident.CorrelationKey);
        Assert.Equal(IncidentState.Open, incident.State);
        Assert.Equal(normalizedEvent.Id, Assert.Single(incident.EventIds));
        Assert.Single(incident.Evidence);
        Assert.NotNull(await store.GetAsync(incident.Id));
    }

    [Fact]
    public async Task Second_scored_event_same_ip_appends_and_extends_window()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator([new UriContainsRule("hostile")], store, new CorrelationOptions());
        var first = HttpEvent("/hostile/one", "198.51.100.2", Start);
        var second = HttpEvent("/hostile/two", "198.51.100.2", Start.AddMinutes(5));

        await correlator.CorrelateAsync(first);
        var incidents = await correlator.CorrelateAsync(second);

        var incident = Assert.Single(incidents);
        Assert.Equal(2, incident.EventIds.Count);
        Assert.Contains(first.Id, incident.EventIds);
        Assert.Contains(second.Id, incident.EventIds);
        Assert.Equal(first.OccurredAt, incident.WindowStart);
        Assert.Equal(second.OccurredAt, incident.WindowEnd);
        Assert.Equal(2, incident.Evidence.Count);
    }

    [Fact]
    public async Task Different_ip_creates_separate_incident()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator([new UriContainsRule("hostile")], store, new CorrelationOptions());

        var first = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.3", Start)));
        var second = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/two", "198.51.100.4", Start.AddMinutes(1))));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("ip=198.51.100.3", first.CorrelationKey);
        Assert.Equal("ip=198.51.100.4", second.CorrelationKey);
    }

    [Fact]
    public async Task Benign_event_does_not_create_incident()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator([new UriContainsRule("hostile")], store, new CorrelationOptions());

        var incidents = await correlator.CorrelateAsync(HttpEvent("/", "198.51.100.5", Start));

        Assert.Empty(incidents);
    }

    [Fact]
    public async Task Existing_incident_accepts_evidence_free_context_event()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator([new UriContainsRule("hostile")], store, new CorrelationOptions());
        var first = HttpEvent("/hostile/one", "198.51.100.6", Start);
        var context = HttpEvent("/", "198.51.100.6", Start.AddMinutes(2));

        await correlator.CorrelateAsync(first);
        var incidents = await correlator.CorrelateAsync(context);

        var incident = Assert.Single(incidents);
        Assert.Equal(2, incident.EventIds.Count);
        Assert.Single(incident.Evidence);
    }

    [Fact]
    public async Task Mdaemon_event_correlates_by_ip_entity()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator([new MDaemonDetectionRule()], store, new CorrelationOptions());
        var normalizedEvent = MDaemonEvent("203.0.113.20", Start);

        var incidents = await correlator.CorrelateAsync(normalizedEvent);

        var incident = Assert.Single(incidents);
        Assert.Equal("ip=203.0.113.20", incident.CorrelationKey);
        Assert.Equal(normalizedEvent.Id, Assert.Single(incident.EventIds));
    }

    [Fact]
    public async Task Window_expiry_creates_new_incident_for_same_ip()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator(
            [new UriContainsRule("hostile")],
            store,
            new CorrelationOptions { WindowDuration = TimeSpan.FromMinutes(10) });

        var first = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.7", Start)));
        var second = Assert.Single(await correlator.CorrelateAsync(HttpEvent("/hostile/two", "198.51.100.7", Start.AddMinutes(21))));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Single(first.EventIds);
        Assert.Single(second.EventIds);
    }

    [Fact]
    public async Task Incident_event_and_evidence_caps_are_enforced()
    {
        var store = new InMemoryIncidentStore();
        var correlator = new TimeWindowCorrelator(
            [new UriContainsRule("hostile", evidenceCount: 2)],
            store,
            new CorrelationOptions
            {
                WindowDuration = TimeSpan.FromMinutes(10),
                MaxEventIdsPerIncident = 2,
                MaxEvidenceItemsPerIncident = 3,
            });

        await correlator.CorrelateAsync(HttpEvent("/hostile/one", "198.51.100.8", Start));
        await correlator.CorrelateAsync(HttpEvent("/hostile/two", "198.51.100.8", Start.AddMinutes(1)));
        var incidents = await correlator.CorrelateAsync(HttpEvent("/hostile/three", "198.51.100.8", Start.AddMinutes(2)));

        var incident = Assert.Single(incidents);
        Assert.Equal(2, incident.EventIds.Count);
        Assert.Equal(3, incident.Evidence.Count);
        Assert.Contains(incident.Evidence, e => e.Description.Contains("Evidence truncated", StringComparison.Ordinal));
    }

    private static NormalizedEvent HttpEvent(string uri, string remoteAddress, DateTimeOffset occurredAt)
    {
        var id = Guid.NewGuid();
        return new NormalizedEvent
        {
            Id = id,
            SourceId = "nginx-test",
            SourceType = "nginx",
            OccurredAt = occurredAt,
            Entities =
            [
                new EntityRef(EntityKind.IpAddress, remoteAddress),
            ],
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
    }

    private static NormalizedEvent MDaemonEvent(string remoteIp, DateTimeOffset occurredAt)
    {
        var id = Guid.NewGuid();
        return new NormalizedEvent
        {
            Id = id,
            SourceId = "mdaemon-test",
            SourceType = "mdaemon",
            OccurredAt = occurredAt,
            Entities =
            [
                new EntityRef(EntityKind.IpAddress, remoteIp),
            ],
            Payload = new MDaemonLogEvent
            {
                LogKind = MDaemonLogKind.DynamicScreening,
                EventKind = MDaemonEventKind.IpBlocked,
                RemoteIp = remoteIp,
                Message = "sanitized MDaemon log line",
            },
            RawObservationId = Guid.NewGuid(),
        };
    }

    private sealed class UriContainsRule(string marker, int evidenceCount = 1) : IDetectionRule
    {
        public string RuleId => "test.uri-contains";

        public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
        {
            if (e.Payload is not HttpRequestEvent http
                || http.Uri is null
                || !http.Uri.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            return Enumerable.Range(1, evidenceCount)
                .Select(i => new EvidenceItem
                {
                    Description = $"test evidence {i}",
                    Score = 0.5,
                    EventId = e.Id,
                })
                .ToList();
        }
    }
}
