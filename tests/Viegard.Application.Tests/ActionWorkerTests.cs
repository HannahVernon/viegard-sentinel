using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Viegard.Actions.MikroTik;
using Viegard.Application.Actions;
using Viegard.Application.Queues;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class ActionWorkerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Unknown_provider_marks_action_failed_and_completes_lease()
    {
        var store = new InMemoryActionStore();
        var queue = new ChannelWorkQueue<ActionWorkItem>("actions");
        var audit = new InMemoryAuditLedger();
        var action = PendingAction(providerId: "unknown");
        await store.UpsertAsync(action);
        await queue.EnqueueAsync(new ActionWorkItem(action.Id));
        var worker = Worker(queue, store, [], audit);

        await worker.ProcessNextAsync();

        var restored = await store.GetAsync(action.Id);
        Assert.NotNull(restored);
        Assert.Equal(ActionStatus.Failed, restored!.Status);
        Assert.Contains("Unknown action provider", restored.Error, StringComparison.Ordinal);
        var stats = await queue.GetStatsAsync();
        Assert.Equal(1, stats.TotalCompleted);
        Assert.Equal(0, stats.Depth);
        Assert.Single(audit.Snapshot());
    }

    [Fact]
    public async Task Missing_action_record_completes_lease_without_audit()
    {
        var store = new InMemoryActionStore();
        var queue = new ChannelWorkQueue<ActionWorkItem>("actions");
        var audit = new InMemoryAuditLedger();
        await queue.EnqueueAsync(new ActionWorkItem(ViegardId.New()));
        var worker = Worker(queue, store, [], audit);

        await worker.ProcessNextAsync();

        var stats = await queue.GetStatsAsync();
        Assert.Equal(1, stats.TotalCompleted);
        Assert.Empty(audit.Snapshot());
    }

    [Fact]
    public async Task Retry_bound_reached_marks_action_failed_after_five_router_attempts()
    {
        var fixture = new ProviderFixture();
        var router = await fixture.AddRouterAsync("router-a");
        for (var i = 0; i < MikroTikBanActionProvider.MaxAttemptsPerRouter; i++)
        {
            fixture.Http.Enqueue(router.Id, MikroTikActionProviderTests.JsonResponse(HttpStatusCode.InternalServerError, "{}"));
        }

        var store = new InMemoryActionStore();
        var queue = new ChannelWorkQueue<ActionWorkItem>("actions");
        var action = MikroTikActionProviderTests.Action(
            MikroTikBanActionProvider.BanIpOperationId,
            new { ip = "203.0.113.10", timeout = "5m" });
        await store.UpsertAsync(action);
        await queue.EnqueueAsync(new ActionWorkItem(action.Id));
        var worker = Worker(queue, store, [fixture.Provider]);

        for (var i = 0; i < MikroTikBanActionProvider.MaxAttemptsPerRouter; i++)
        {
            await worker.ProcessNextAsync();
        }

        var restored = await store.GetAsync(action.Id);
        Assert.NotNull(restored);
        Assert.Equal(ActionStatus.Failed, restored!.Status);
        var result = Assert.Single(MikroTikActionProviderTests.Results(restored));
        Assert.Equal("failed", result.Status);
        Assert.Equal(MikroTikBanActionProvider.MaxAttemptsPerRouter, result.Attempts);
        var stats = await queue.GetStatsAsync();
        Assert.Equal(MikroTikBanActionProvider.MaxAttemptsPerRouter, stats.TotalCompleted);
        Assert.Equal(MikroTikBanActionProvider.MaxAttemptsPerRouter - 1, stats.TotalEnqueued - 1);
        Assert.Equal(0, stats.Depth);
    }

    [Fact]
    public async Task Resume_does_not_recall_routers_already_marked_applied()
    {
        var fixture = new ProviderFixture();
        var routerA = await fixture.AddRouterAsync("router-a");
        var routerB = await fixture.AddRouterAsync("router-b");
        fixture.Http.Enqueue(routerB.Id, MikroTikActionProviderTests.JsonResponse(HttpStatusCode.OK, "{}"));
        var action = MikroTikActionProviderTests.Action(
            MikroTikBanActionProvider.BanIpOperationId,
            new { ip = "203.0.113.10", timeout = "5m" }) with
            {
                ResultsJson = JsonSerializer.Serialize(
                    new[]
                    {
                        new MikroTikRouterActionResult
                        {
                            RouterId = routerA.Id,
                            RouterName = routerA.Name,
                            Attempts = 1,
                            Status = "applied",
                            Detail = "Ban applied.",
                            LastAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                        },
                        new MikroTikRouterActionResult
                        {
                            RouterId = routerB.Id,
                            RouterName = routerB.Name,
                            Attempts = 1,
                            Status = "failed",
                            Detail = "Router returned HTTP 500.",
                            LastAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                        },
                    },
                    Json),
            };

        var store = new InMemoryActionStore();
        var queue = new ChannelWorkQueue<ActionWorkItem>("actions");
        await store.UpsertAsync(action);
        await queue.EnqueueAsync(new ActionWorkItem(action.Id));
        var worker = Worker(queue, store, [fixture.Provider]);

        await worker.ProcessNextAsync();

        Assert.Single(fixture.Http.Requests);
        Assert.Equal(routerB.Id, Assert.Single(fixture.Http.Requests).RouterId);
        var restored = await store.GetAsync(action.Id);
        Assert.NotNull(restored);
        Assert.Equal(ActionStatus.Succeeded, restored!.Status);
        var results = MikroTikActionProviderTests.Results(restored).OrderBy(result => result.RouterName, StringComparer.Ordinal).ToList();
        Assert.Equal(1, results[0].Attempts);
        Assert.Equal(2, results[1].Attempts);
    }

    private static ActionWorker Worker(
        ChannelWorkQueue<ActionWorkItem> queue,
        InMemoryActionStore store,
        IEnumerable<IActionProvider> providers,
        InMemoryAuditLedger? audit = null) =>
        new(
            queue,
            store,
            providers,
            audit ?? new InMemoryAuditLedger(),
            Options.Create(new ActionWorkerOptions { RetryDelay = TimeSpan.Zero }),
            TimeProvider.System,
            NullLogger<ActionWorker>.Instance);

    private static ActionRecord PendingAction(string providerId) => new()
    {
        Id = ViegardId.New(),
        DecisionId = ViegardId.New(),
        ProviderId = providerId,
        OperationId = "ban-ip",
        ParametersJson = """{"ip":"203.0.113.10","timeout":"5m"}""",
        Status = ActionStatus.Pending,
        RequestedAt = DateTimeOffset.UtcNow,
    };
}
