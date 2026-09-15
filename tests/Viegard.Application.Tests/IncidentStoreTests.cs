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
