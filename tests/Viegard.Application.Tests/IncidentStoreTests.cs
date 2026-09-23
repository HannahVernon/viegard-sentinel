using Viegard.Domain;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class IncidentStoreTests
{
    [Fact]
    public async Task In_memory_find_by_event_id_returns_matching_incidents_only()
    {
        var store = new InMemoryIncidentStore();
        var eventId = ViegardId.New();
        var related = Incident("it-related", eventId);
        var unrelated = Incident("it-unrelated", ViegardId.New());
        await store.UpsertAsync(unrelated);
        await store.UpsertAsync(related);

        var matches = await store.FindByEventIdAsync(eventId);

        Assert.Equal(related.Id, Assert.Single(matches).Id);
        Assert.Empty(await store.FindByEventIdAsync(ViegardId.New()));
    }

    [Fact]
    public async Task FindCoalescibleByCorrelationKeyAsync_returns_only_live_windows()
    {
        var store = new InMemoryIncidentStore();
        var asOf = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var live = Incident("ip=live", ViegardId.New()) with { CoalesceUntil = asOf.AddSeconds(5) };
        var expired = Incident("ip=expired", ViegardId.New()) with { CoalesceUntil = asOf.AddSeconds(-5) };
        var closed = Incident("ip=closed", ViegardId.New()) with { State = IncidentState.Closed, CoalesceUntil = asOf.AddSeconds(5) };
        var noWindow = Incident("ip=nowindow", ViegardId.New()) with { CoalesceUntil = null };
        await store.UpsertAsync(live);
        await store.UpsertAsync(expired);
        await store.UpsertAsync(closed);
        await store.UpsertAsync(noWindow);

        Assert.Equal(live.Id, (await store.FindCoalescibleByCorrelationKeyAsync("ip=live", asOf))!.Id);
        Assert.Null(await store.FindCoalescibleByCorrelationKeyAsync("ip=expired", asOf));
        Assert.Null(await store.FindCoalescibleByCorrelationKeyAsync("ip=closed", asOf));
        Assert.Null(await store.FindCoalescibleByCorrelationKeyAsync("ip=nowindow", asOf));
    }

    [Fact]
    public async Task ListCoalescingReadyAsync_returns_closed_windows_ordered_by_deadline()
    {
        var store = new InMemoryIncidentStore();
        var asOf = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var readyLate = Incident("ip=ready-late", ViegardId.New()) with { CoalesceUntil = asOf.AddSeconds(-1) };
        var readyEarly = Incident("ip=ready-early", ViegardId.New()) with { CoalesceUntil = asOf.AddSeconds(-30) };
        var stillOpen = Incident("ip=still-open", ViegardId.New()) with { CoalesceUntil = asOf.AddSeconds(5) };
        var finalized = Incident("ip=finalized", ViegardId.New()) with { State = IncidentState.Closed, CoalesceUntil = asOf.AddSeconds(-5) };
        await store.UpsertAsync(readyLate);
        await store.UpsertAsync(readyEarly);
        await store.UpsertAsync(stillOpen);
        await store.UpsertAsync(finalized);

        var ready = await store.ListCoalescingReadyAsync(asOf, 10);

        Assert.Equal([readyEarly.Id, readyLate.Id], ready.Select(i => i.Id).ToArray());
    }

    private static Incident Incident(string correlationKey, Guid eventId) => new()
    {
        Id = ViegardId.New(),
        CorrelationKey = correlationKey,
        WindowStart = DateTimeOffset.UtcNow,
        WindowEnd = DateTimeOffset.UtcNow.AddMinutes(1),
        EventIds = [eventId],
        Evidence = [],
        State = IncidentState.Open,
    };
}
