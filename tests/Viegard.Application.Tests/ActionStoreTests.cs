using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class ActionStoreTests
{
    [Fact]
    public async Task In_memory_action_store_round_trips_results_json()
    {
        var store = new InMemoryActionStore();
        var action = new ActionRecord
        {
            Id = ViegardId.New(),
            DecisionId = ViegardId.New(),
            ProviderId = "mikrotik",
            OperationId = "ban-ip",
            ParametersJson = """{"ip":"203.0.113.10","timeout":"5m"}""",
            ResultsJson = """[{"routerId":"018f6ad8-98e8-7b71-a62c-2f41829f2e41","routerName":"router-a","attempts":1,"status":"applied","detail":"Ban applied.","lastAttemptAt":"2026-09-16T20:45:00+00:00"}]""",
            Status = ActionStatus.Succeeded,
            RequestedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await store.UpsertAsync(action);

        var restored = await store.GetAsync(action.Id);
        Assert.NotNull(restored);
        Assert.Equal(action.ResultsJson, restored!.ResultsJson);
    }

    [Fact]
    public async Task In_memory_action_store_lists_recent_actions_for_provider_with_limit()
    {
        var store = new InMemoryActionStore();
        var now = DateTimeOffset.UtcNow;
        var oldMikroTik = Action("mikrotik", now.AddMinutes(-2));
        var other = Action("other", now.AddMinutes(-1));
        var newMikroTik = Action("mikrotik", now);
        await store.AddAsync(oldMikroTik);
        await store.AddAsync(other);
        await store.AddAsync(newMikroTik);

        var recent = await store.ListRecentByProviderAsync("mikrotik", limit: 1);

        Assert.Equal(newMikroTik.Id, Assert.Single(recent).Id);
    }

    private static ActionRecord Action(string providerId, DateTimeOffset requestedAt) => new()
    {
        Id = ViegardId.New(),
        DecisionId = ViegardId.New(),
        ProviderId = providerId,
        OperationId = "ban-ip",
        ParametersJson = """{"ip":"203.0.113.10","timeout":"5m"}""",
        Status = ActionStatus.Pending,
        RequestedAt = requestedAt,
    };
}
