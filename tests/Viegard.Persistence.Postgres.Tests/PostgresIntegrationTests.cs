using Microsoft.EntityFrameworkCore;
using Viegard.Domain.Events;
using Viegard.Persistence.Postgres.Queues;
using Viegard.Persistence.Postgres.Stores;

namespace Viegard.Persistence.Postgres.Tests;

/// <summary>
/// Live-database integration tests (skipped unless VIEGARD_TEST_POSTGRES is
/// set; see TestDatabase).  Each run migrates the schema, then exercises the
/// durable queue semantics and a store round-trip.
/// </summary>
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private Npgsql.NpgsqlDataSource? _dataSource;

    public async Task InitializeAsync()
    {
        if (TestDatabase.ConnectionString is null)
        {
            return;
        }

        _dataSource = TestDatabase.CreateDataSource();

        // The schema must exist before the first search_path-relative
        // statement runs, mirroring MigrateViegardDatabaseAsync.
        await using (var create = _dataSource.CreateCommand(
            $"CREATE SCHEMA IF NOT EXISTS {TestDatabase.Schema}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var builder = new DbContextOptionsBuilder<ViegardDbContext>();
        ViegardDbContextConfiguration.Configure(builder, _dataSource, TestDatabase.Schema);
        await using var db = new ViegardDbContext(builder.Options);
        await db.Database.MigrateAsync(CancellationToken.None);

        // Clean slate for queue tables between runs.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE queue_messages, queue_counters");
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    private PostgresWorkQueue<Guid> CreateQueue(string name, int maxDeliveries = 5, TimeSpan? lease = null) =>
        new(_dataSource!, name, maxDeliveries, lease);

    [PostgresFact]
    public async Task Enqueue_lease_complete_roundtrip()
    {
        var queue = CreateQueue("it-basic");
        var message = Guid.NewGuid();

        await queue.EnqueueAsync(message);
        var lease = await queue.LeaseAsync(CancellationToken.None);

        Assert.Equal(message, lease.Message);
        Assert.Equal(1, lease.DeliveryCount);
        await lease.CompleteAsync();

        var stats = await queue.GetStatsAsync();
        Assert.Equal(0, stats.Depth);
        Assert.Equal(0, stats.InFlight);
        Assert.Equal(1, stats.TotalEnqueued);
        Assert.Equal(1, stats.TotalCompleted);
    }

    [PostgresFact]
    public async Task Abandon_redelivers_with_incremented_count()
    {
        var queue = CreateQueue("it-abandon");
        await queue.EnqueueAsync(Guid.NewGuid());

        var first = await queue.LeaseAsync(CancellationToken.None);
        await first.AbandonAsync();

        var second = await queue.LeaseAsync(CancellationToken.None);
        Assert.Equal(2, second.DeliveryCount);
        await second.CompleteAsync();
    }

    [PostgresFact]
    public async Task Message_exceeding_max_deliveries_is_dead_lettered()
    {
        var queue = CreateQueue("it-poison", maxDeliveries: 2);
        await queue.EnqueueAsync(Guid.NewGuid());

        var first = await queue.LeaseAsync(CancellationToken.None);
        await first.AbandonAsync();
        var second = await queue.LeaseAsync(CancellationToken.None);
        await second.AbandonAsync();

        var stats = await queue.GetStatsAsync();
        Assert.Equal(1, stats.DeadLetterCount);
        Assert.Equal(0, stats.Depth);
    }

    [PostgresFact]
    public async Task Expired_lease_is_redelivered_for_crash_recovery()
    {
        var queue = CreateQueue("it-expiry", lease: TimeSpan.FromMilliseconds(200));
        await queue.EnqueueAsync(Guid.NewGuid());

        // Lease and never settle: simulates a crashed consumer.
        _ = await queue.LeaseAsync(CancellationToken.None);
        await Task.Delay(400);

        var redelivered = await queue.LeaseAsync(CancellationToken.None);
        Assert.Equal(2, redelivered.DeliveryCount);
        await redelivered.CompleteAsync();
    }

    [PostgresFact]
    public async Task Concurrent_consumers_never_double_lease()
    {
        var queue = CreateQueue("it-concurrent");
        var messages = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToHashSet();
        foreach (var message in messages)
        {
            await queue.EnqueueAsync(message);
        }

        var received = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var consumers = Enumerable.Range(0, 5).Select(async _ =>
        {
            for (var i = 0; i < 10; i++)
            {
                var lease = await queue.LeaseAsync(CancellationToken.None);
                received.Add(lease.Message);
                await lease.CompleteAsync();
            }
        });
        await Task.WhenAll(consumers);

        Assert.Equal(50, received.Count);
        Assert.Equal(messages, received.ToHashSet());
    }

    [PostgresFact]
    public async Task Listen_notify_wakes_a_waiting_consumer()
    {
        var queue = CreateQueue("it-notify");

        var leaseTask = queue.LeaseAsync(CancellationToken.None).AsTask();
        await Task.Delay(300);
        Assert.False(leaseTask.IsCompleted);

        var message = Guid.NewGuid();
        await queue.EnqueueAsync(message);

        var lease = await leaseTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(message, lease.Message);
        await lease.CompleteAsync();
    }

    [PostgresFact]
    public async Task Event_store_round_trips_polymorphic_payloads()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresEventStore(factory);
        var observationStore = new PostgresRawObservationStore(factory);

        var observation = new RawObservation
        {
            Id = Guid.NewGuid(),
            SourceId = "it:source",
            ObservedAt = DateTimeOffset.UtcNow,
            PayloadReference = $"it/{Guid.NewGuid():N}",
        };
        await observationStore.AddAsync(observation, "raw-payload");

        var normalizedEvent = new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = "it:source",
            SourceType = "syslog",
            OccurredAt = DateTimeOffset.UtcNow,
            Entities = [new EntityRef(EntityKind.IpAddress, "203.0.113.7")],
            Payload = new HttpRequestEvent { RemoteAddress = "203.0.113.7", Uri = "/.env", StatusCode = 404 },
            RawObservationId = observation.Id,
        };
        await store.AddAsync(normalizedEvent);

        var restored = await store.GetAsync(normalizedEvent.Id);
        Assert.NotNull(restored);
        var http = Assert.IsType<HttpRequestEvent>(restored.Payload);
        Assert.Equal("/.env", http.Uri);
        Assert.Equal(normalizedEvent.Entities, restored.Entities);

        Assert.Equal("raw-payload", await observationStore.GetPayloadAsync(observation.PayloadReference));
    }

    private sealed class TestDbContextFactory(Npgsql.NpgsqlDataSource dataSource) : IDbContextFactory<ViegardDbContext>
    {
        public ViegardDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<ViegardDbContext>();
            ViegardDbContextConfiguration.Configure(builder, dataSource, TestDatabase.Schema);
            return new ViegardDbContext(builder.Options);
        }
    }
}
