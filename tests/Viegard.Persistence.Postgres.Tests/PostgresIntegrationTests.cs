using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Viegard.Application.Configuration;
using Viegard.Application.Detection;
using Viegard.Application.Policy;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Configuration;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Health;
using Viegard.Domain.Incidents;
using Viegard.Persistence.Postgres.Model;
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
            "TRUNCATE actions, active_bans, queue_messages, queue_counters, retention_settings, policy_threshold_settings, mikrotik_routers, host_upgrade_commands, ingestion_filters, instance_registry");
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

    private async Task<int> QueueDeliveryCountAsync(string queueName)
    {
        await using var command = _dataSource!.CreateCommand(
            "SELECT COALESCE(MAX(delivery_count), 0) FROM queue_messages WHERE queue_name = @queue");
        command.Parameters.AddWithValue("queue", queueName);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    [PostgresFact]
    public async Task Satellite_role_lifecycle_manages_role_password_and_grants()
    {
        var store = CreateSatelliteStore(_dataSource!);
        var satelliteName = "it" + Guid.NewGuid().ToString("N")[..12];
        var roleName = SatelliteRoleName.RolePrefix + satelliteName;
        await CleanupSatelliteRoleAsync(roleName);

        try
        {
            var created = await store.CreateAsync(satelliteName);
            Assert.Equal(roleName, created.RoleName);
            Assert.True(SatelliteRolePassword.UsesAlphabet(created.Password));

            var listed = await store.ListAsync();
            var role = Assert.Single(listed, candidate => candidate.RoleName == roleName);
            Assert.True(role.CanLogin);

            await AssertCanAuthenticateAsync(created);
            await AssertSatelliteGrantsAsync(roleName);

            var rotated = await store.RotatePasswordAsync(satelliteName);
            Assert.Equal(roleName, rotated.RoleName);
            Assert.NotEqual(created.Password, rotated.Password);
            await AssertCanAuthenticateAsync(rotated);

            await store.RevokeAsync(satelliteName);

            var afterRevoke = await store.ListAsync();
            Assert.DoesNotContain(afterRevoke, candidate => candidate.RoleName == roleName);
            Assert.False(await RoleExistsAsync(roleName));
        }
        finally
        {
            await CleanupSatelliteRoleAsync(roleName);
        }
    }

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
    public async Task Abandon_without_charging_refunds_delivery_count()
    {
        const string queueName = "it-abandon-uncharged";
        var queue = CreateQueue(queueName, maxDeliveries: 2);
        await queue.EnqueueAsync(Guid.NewGuid());
        Assert.Equal(0, await QueueDeliveryCountAsync(queueName));

        var first = await queue.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, first.DeliveryCount);
        Assert.Equal(1, await QueueDeliveryCountAsync(queueName));

        await first.AbandonAsync(chargeAttempt: false);

        Assert.Equal(0, await QueueDeliveryCountAsync(queueName));
        var second = await queue.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, second.DeliveryCount);
        var stats = await queue.GetStatsAsync();
        Assert.Equal(0, stats.TotalAbandoned);
        Assert.Equal(0, stats.DeadLetterCount);
        await second.CompleteAsync();
    }

    [PostgresFact]
    public async Task Repeated_uncharged_abandons_never_dead_letter()
    {
        const string queueName = "it-abandon-repeat-uncharged";
        var queue = CreateQueue(queueName, maxDeliveries: 2);
        await queue.EnqueueAsync(Guid.NewGuid());

        for (var i = 0; i < 5; i++)
        {
            var lease = await queue.LeaseAsync(CancellationToken.None);
            await lease.AbandonAsync(chargeAttempt: false);
        }

        var redelivery = await queue.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, redelivery.DeliveryCount);
        var stats = await queue.GetStatsAsync();
        Assert.Equal(0, stats.TotalAbandoned);
        Assert.Equal(0, stats.DeadLetterCount);
        await redelivery.CompleteAsync();
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
    public async Task Host_upgrade_store_enforces_single_flight_and_cooldown()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero));
        var store = new PostgresHostUpgradeCommandStore(_dataSource!, time);

        var requested = await store.RequestAsync("vm", "hannah");

        var pendingReject = await Assert.ThrowsAsync<HostUpgradeCommandRejectedException>(async () =>
            await store.RequestAsync("vm", "hannah"));
        Assert.Equal(HostUpgradeCommandRejectionReason.SingleFlight, pendingReject.Reason);

        var claimed = await store.ClaimNextPendingAsync("vm");
        Assert.NotNull(claimed);
        Assert.Equal(requested.Id, claimed.Id);
        Assert.Equal(HostUpgradeCommandStatus.Running, claimed.Status);

        var runningReject = await Assert.ThrowsAsync<HostUpgradeCommandRejectedException>(async () =>
            await store.RequestAsync("vm", "hannah"));
        Assert.Equal(HostUpgradeCommandRejectionReason.SingleFlight, runningReject.Reason);

        var completed = await store.CompleteAsync(requested.Id, succeeded: false, detail: "upgrade failed");
        Assert.NotNull(completed);
        Assert.Equal(HostUpgradeCommandStatus.Failed, completed.Status);
        Assert.Equal("upgrade failed", completed.Detail);

        var cooldownReject = await Assert.ThrowsAsync<HostUpgradeCommandRejectedException>(async () =>
            await store.RequestAsync("vm", "hannah"));
        Assert.Equal(HostUpgradeCommandRejectionReason.Cooldown, cooldownReject.Reason);

        time.Advance(HostUpgradeCommandPolicy.Cooldown.Add(TimeSpan.FromSeconds(1)));
        var next = await store.RequestAsync("vm", "hannah");
        Assert.Equal(HostUpgradeCommandStatus.Pending, next.Status);

        var recent = await store.ListRecentAsync("vm", limit: 10);
        Assert.Equal(next.Id, recent[0].Id);
        Assert.Contains(recent, command => command.Id == requested.Id);
    }

    [PostgresFact]
    public async Task Host_upgrade_store_lists_default_target_then_distinct_recent_targets()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 16, 22, 0, 0, TimeSpan.Zero));
        var store = new PostgresHostUpgradeCommandStore(_dataSource!, time);

        var firstSatellite = await store.RequestAsync("sat-a", "hannah");
        Assert.NotNull(await store.ClaimNextPendingAsync("sat-a"));
        await store.CompleteAsync(firstSatellite.Id, succeeded: true, detail: "ok");

        time.Advance(HostUpgradeCommandPolicy.Cooldown.Add(TimeSpan.FromSeconds(1)));
        var secondSatellite = await store.RequestAsync("sat-b", "hannah");
        Assert.NotNull(await store.ClaimNextPendingAsync("sat-b"));
        await store.CompleteAsync(secondSatellite.Id, succeeded: true, detail: "ok");

        time.Advance(HostUpgradeCommandPolicy.Cooldown.Add(TimeSpan.FromSeconds(1)));
        await store.RequestAsync("sat-a", "hannah");

        var targets = await store.ListTargetsAsync();

        Assert.Equal(["vm", "sat-a", "sat-b"], targets);
    }

    [PostgresFact]
    public async Task Host_upgrade_known_targets_include_instance_registry_targets()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 16, 22, 0, 0, TimeSpan.Zero));
        var commandStore = new PostgresHostUpgradeCommandStore(_dataSource!, time);
        var factory = new TestDbContextFactory(_dataSource!);
        var registry = new PostgresInstanceRegistryStore(factory);

        await registry.UpsertAsync(InstanceRegistration("pipeline-1", "1.0.0", time.GetUtcNow()) with { UpgradeTarget = null });
        await registry.UpsertAsync(InstanceRegistration("satellite-b", "1.0.0", time.GetUtcNow()) with { UpgradeTarget = "sat-b" });
        await registry.UpsertAsync(InstanceRegistration("satellite-a", "1.0.0", time.GetUtcNow()) with { UpgradeTarget = "sat-a" });

        var requested = await commandStore.RequestAsync("sat-c", "hannah");
        Assert.NotNull(await commandStore.ClaimNextPendingAsync("sat-c"));
        await commandStore.CompleteAsync(requested.Id, succeeded: true, detail: "ok");

        var targets = HostUpgradeTargetList.BuildKnownTargets(
            await commandStore.ListTargetsAsync(),
            await registry.ListAsync());

        Assert.Equal(["vm", "sat-a", "sat-b", "sat-c"], targets);
    }

    [PostgresFact]
    public async Task Instance_registry_store_upserts_and_lists()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresInstanceRegistryStore(factory);
        var now = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
        var first = InstanceRegistration("pipeline-b", "1.0.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", now);
        var second = InstanceRegistration("pipeline-a", "1.0.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", now);
        var updatedFirst = first with
        {
            Version = "1.0.1+cccccccccccccccccccccccccccccccccccccccc",
            CommitSha = "cccccccccccccccccccccccccccccccccccccccc",
            Roles = "sources,correlation",
            UpgradeTarget = "satellite-b",
            ReportedAt = now.AddMinutes(1),
        };

        await store.UpsertAsync(first);
        await store.UpsertAsync(second);
        await store.UpsertAsync(updatedFirst);

        var registrations = await store.ListAsync();

        Assert.Equal(["pipeline-a", "pipeline-b"], registrations.Select(r => r.InstanceId).ToArray());
        var restored = registrations.Single(r => r.InstanceId == "pipeline-b");
        Assert.Equal("1.0.1+cccccccccccccccccccccccccccccccccccccccc", restored.Version);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", restored.CommitSha);
        Assert.Equal("sources,correlation", restored.Roles);
        Assert.Equal("satellite-b", restored.UpgradeTarget);
        Assert.Equal(now.AddMinutes(1), restored.ReportedAt);
    }

    [PostgresFact]
    public async Task Instance_registry_store_deletes_only_rows_reported_before_the_cutoff()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresInstanceRegistryStore(factory);
        var now = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
        await store.UpsertAsync(InstanceRegistration("admin-dead", "1.0.0", now) with { ReportedAt = now.AddHours(-30) });
        await store.UpsertAsync(InstanceRegistration("admin", "1.0.0", now));
        await store.UpsertAsync(InstanceRegistration("pipeline-1", "1.0.0", now) with { ReportedAt = now.AddMinutes(-1) });

        var removed = await store.DeleteStaleAsync(now.AddHours(-24));

        Assert.Equal(1, removed);
        var remaining = await store.ListAsync();
        Assert.Equal(["admin", "pipeline-1"], remaining.Select(r => r.InstanceId).ToArray());
    }

    [PostgresFact]
    public async Task Concurrent_host_upgrade_claims_never_claim_same_command_twice()
    {
        var store = new PostgresHostUpgradeCommandStore(_dataSource!);
        var requested = await store.RequestAsync("vm", "hannah");

        var claimTasks = Enumerable.Range(0, 10)
            .Select(_ => store.ClaimNextPendingAsync("vm").AsTask())
            .ToArray();
        var claims = await Task.WhenAll(claimTasks);
        var nonNullClaims = claims.Where(command => command is not null).ToList();

        var claimed = Assert.Single(nonNullClaims);
        Assert.NotNull(claimed);
        Assert.Equal(requested.Id, claimed.Id);
        Assert.Equal(HostUpgradeCommandStatus.Running, claimed.Status);
        Assert.Null(await store.ClaimNextPendingAsync("vm"));

        var completed = await store.CompleteAsync(requested.Id, succeeded: true, detail: "ok");
        Assert.NotNull(completed);
        Assert.Equal(HostUpgradeCommandStatus.Succeeded, completed.Status);
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
    public async Task Raw_observation_store_reports_replayed_payload_references()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var store = new PostgresRawObservationStore(factory, resolver);
        var observation = new RawObservation
        {
            Id = Guid.NewGuid(),
            SourceId = $"it:source-{ViegardId.New():N}",
            SourceType = "syslog",
            ObservedAt = DateTimeOffset.UtcNow,
            PayloadReference = $"it/{Guid.NewGuid():N}",
        };

        Assert.True(await store.AddAsync(observation, "raw-payload"));
        Assert.False(await store.AddAsync(observation with { Id = Guid.NewGuid() }, "replayed-payload"));

        Assert.Equal("raw-payload", await store.GetPayloadAsync(observation.PayloadReference));
    }

    [PostgresFact]
    public async Task Event_store_round_trips_polymorphic_payloads()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var store = new PostgresEventStore(factory, resolver);
        var observationStore = new PostgresRawObservationStore(factory, resolver);
        var sourceKey = $"it:source-{ViegardId.New():N}";

        var observation = new RawObservation
        {
            Id = Guid.NewGuid(),
            SourceId = sourceKey,
            SourceType = "syslog",
            ObservedAt = DateTimeOffset.UtcNow,
            PayloadReference = $"it/{Guid.NewGuid():N}",
        };
        await observationStore.AddAsync(observation, "raw-payload");

        var normalizedEvent = new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = sourceKey,
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
        Assert.Equal(sourceKey, restored.SourceId);
        Assert.Equal("syslog", restored.SourceType);

        Assert.Equal("raw-payload", await observationStore.GetPayloadAsync(observation.PayloadReference));

        var filtered = await store.ListPageAsync(beforeId: null, pageSize: 10, filter: new EventListFilter(sourceKey));
        Assert.Equal(normalizedEvent.Id, Assert.Single(filtered.Items).Id);
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(0, filtered.Preceding);
    }

    [PostgresFact]
    public async Task Incident_store_finds_incidents_containing_event_id()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresIncidentStore(factory);
        var eventId = ViegardId.New();
        var now = DateTimeOffset.UtcNow;
        var related = Incident($"it-event-link-{ViegardId.New():N}", now) with
        {
            EventIds = [ViegardId.New(), eventId],
        };
        var unrelated = Incident($"it-event-link-{ViegardId.New():N}", now.AddMinutes(1)) with
        {
            EventIds = [ViegardId.New()],
        };
        await store.UpsertAsync(unrelated);
        await store.UpsertAsync(related);

        var matches = await store.FindByEventIdAsync(eventId);

        Assert.Equal(related.Id, Assert.Single(matches).Id);
        Assert.Empty(await store.FindByEventIdAsync(ViegardId.New()));
    }

    [PostgresFact]
    public async Task Admin_list_sorts_use_keyset_ordering_for_the_filtered_result_set()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var prefix = $"it-sort-{ViegardId.New():N}";
        var now = DateTimeOffset.UtcNow;

        var eventStore = new PostgresEventStore(factory, resolver);
        var eventB = Event($"{prefix}-source-b", now.AddMinutes(2));
        var eventA = Event($"{prefix}-source-a", now.AddMinutes(1));
        var eventC = Event($"{prefix}-source-c", now.AddMinutes(3));
        foreach (var item in new[] { eventB, eventA, eventC })
        {
            await eventStore.AddAsync(item);
        }

        var eventFilter = new EventListFilter(prefix);
        var eventSort = new ListSort<EventSortColumn>(EventSortColumn.Source, SortDirection.Asc);
        var eventPage1 = await eventStore.ListPageAsync(beforeId: null, pageSize: 2, filter: eventFilter, sort: eventSort);
        var eventPage2 = await eventStore.ListPageAsync(eventPage1.NextCursor, pageSize: 2, filter: eventFilter, sort: eventSort);
        Assert.Equal([eventA.Id, eventB.Id], eventPage1.Items.Select(e => e.Id));
        Assert.Equal(eventC.Id, Assert.Single(eventPage2.Items).Id);
        Assert.Equal(3, eventPage1.TotalCount);
        Assert.Equal(2, eventPage2.Preceding);
        var eventJumpCursor = await eventStore.GetPageCursorAsync(2, 2, eventFilter, eventSort);
        var eventJumpPage = await eventStore.ListPageAsync(eventJumpCursor, pageSize: 2, filter: eventFilter, sort: eventSort);
        Assert.Equal(eventPage2.Items.Select(e => e.Id), eventJumpPage.Items.Select(e => e.Id));

        var incidentStore = new PostgresIncidentStore(factory);
        var incidentB = Incident($"{prefix}-incident-b", now.AddMinutes(2));
        var incidentA = Incident($"{prefix}-incident-a", now.AddMinutes(1));
        var incidentC = Incident($"{prefix}-incident-c", now.AddMinutes(3));
        foreach (var item in new[] { incidentB, incidentA, incidentC })
        {
            await incidentStore.UpsertAsync(item);
        }

        var incidentFilter = new IncidentListFilter(prefix, null);
        var incidentSort = new ListSort<IncidentSortColumn>(IncidentSortColumn.CorrelationKey, SortDirection.Asc);
        var incidentPage1 = await incidentStore.ListPageAsync(beforeId: null, pageSize: 2, filter: incidentFilter, sort: incidentSort);
        var incidentPage2 = await incidentStore.ListPageAsync(incidentPage1.NextCursor, pageSize: 2, filter: incidentFilter, sort: incidentSort);
        Assert.Equal([incidentA.Id, incidentB.Id], incidentPage1.Items.Select(i => i.Id));
        Assert.Equal(incidentC.Id, Assert.Single(incidentPage2.Items).Id);
        Assert.Equal(3, incidentPage1.TotalCount);
        Assert.Equal(2, incidentPage2.Preceding);
        Assert.Null(await incidentStore.GetPageCursorAsync(0, 2, incidentFilter, incidentSort));
        var incidentJumpCursor = await incidentStore.GetPageCursorAsync(2, 2, incidentFilter, incidentSort);
        var incidentJumpPage = await incidentStore.ListPageAsync(incidentJumpCursor, pageSize: 2, filter: incidentFilter, sort: incidentSort);
        Assert.Equal(incidentPage2.Items.Select(i => i.Id), incidentJumpPage.Items.Select(i => i.Id));

        var classificationStore = new PostgresClassificationStore(factory, resolver);
        var decisionStore = new PostgresDecisionStore(factory, resolver);
        var classificationB = CreateClassification($"{prefix}-classification-b", "middle", 5, now.AddMinutes(2));
        var classificationA = CreateClassification($"{prefix}-classification-a", "alpha", 2, now.AddMinutes(1));
        var classificationC = CreateClassification($"{prefix}-classification-c", "zeta", 8, now.AddMinutes(3));
        foreach (var item in new[] { classificationB, classificationA, classificationC })
        {
            await classificationStore.AddAsync(item);
        }

        var decisionB = Decision(classificationB.Id, $"{prefix}-policy-b", DecisionOutcome.RequireApproval, prefix, now.AddMinutes(2));
        var decisionA = Decision(classificationA.Id, $"{prefix}-policy-a", DecisionOutcome.DryRun, prefix, now.AddMinutes(1));
        var decisionC = Decision(classificationC.Id, $"{prefix}-policy-c", DecisionOutcome.Permit, prefix, now.AddMinutes(3));
        foreach (var item in new[] { decisionB, decisionA, decisionC })
        {
            await decisionStore.AddAsync(item);
        }

        var decisionFilter = new DecisionListFilter(prefix, null);
        var policySorted = await decisionStore.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: decisionFilter,
            sort: new ListSort<DecisionSortColumn>(DecisionSortColumn.Policy, SortDirection.Asc));
        Assert.Equal([decisionA.Id, decisionB.Id, decisionC.Id], policySorted.Items.Select(d => d.Id));

        var classificationSort = new ListSort<DecisionSortColumn>(DecisionSortColumn.Classification, SortDirection.Asc);
        var decisionPage1 = await decisionStore.ListPageAsync(beforeId: null, pageSize: 2, filter: decisionFilter, sort: classificationSort);
        var decisionPage2 = await decisionStore.ListPageAsync(decisionPage1.NextCursor, pageSize: 2, filter: decisionFilter, sort: classificationSort);
        Assert.Equal([decisionA.Id, decisionB.Id], decisionPage1.Items.Select(d => d.Id));
        Assert.Equal(decisionC.Id, Assert.Single(decisionPage2.Items).Id);
        Assert.Equal(3, decisionPage1.TotalCount);
        Assert.Equal(2, decisionPage2.Preceding);
        var decisionJumpCursor = await decisionStore.GetPageCursorAsync(2, 2, decisionFilter, classificationSort);
        var decisionJumpPage = await decisionStore.ListPageAsync(decisionJumpCursor, pageSize: 2, filter: decisionFilter, sort: classificationSort);
        Assert.Equal(decisionPage2.Items.Select(d => d.Id), decisionJumpPage.Items.Select(d => d.Id));

        var auditLedger = new PostgresAuditLedger(factory, resolver);
        var auditNone = AuditRecord($"{prefix} audit none", null, now.AddMinutes(1));
        var auditA = AuditRecord($"{prefix} audit a", $"{prefix}-audit-a", now.AddMinutes(2));
        var auditB = AuditRecord($"{prefix} audit b", $"{prefix}-audit-b", now.AddMinutes(3));
        foreach (var item in new[] { auditB, auditNone, auditA })
        {
            await auditLedger.AppendAsync(item);
        }

        var auditFilter = new AuditListFilter(prefix, null);
        var auditSort = new ListSort<AuditSortColumn>(AuditSortColumn.Source, SortDirection.Asc);
        var auditPage1 = await auditLedger.ListPageAsync(beforeId: null, pageSize: 2, filter: auditFilter, sort: auditSort);
        var auditPage2 = await auditLedger.ListPageAsync(auditPage1.NextCursor, pageSize: 2, filter: auditFilter, sort: auditSort);
        Assert.Equal([auditNone.Id, auditA.Id], auditPage1.Items.Select(a => a.Id));
        Assert.Equal(auditB.Id, Assert.Single(auditPage2.Items).Id);
        Assert.Equal(3, auditPage1.TotalCount);
        Assert.Equal(2, auditPage2.Preceding);
        var auditJumpCursor = await auditLedger.GetPageCursorAsync(2, 2, auditFilter, auditSort);
        var auditJumpPage = await auditLedger.ListPageAsync(auditJumpCursor, pageSize: 2, filter: auditFilter, sort: auditSort);
        Assert.Equal(auditPage2.Items.Select(a => a.Id), auditJumpPage.Items.Select(a => a.Id));

        var signatureStore = new PostgresCustomSignatureStore(factory, _dataSource!);
        var signatureB = CustomSignature($"{prefix}-signature-b");
        var signatureA = CustomSignature($"{prefix}-signature-a");
        var signatureC = CustomSignature($"{prefix}-signature-c");
        foreach (var item in new[] { signatureB, signatureA, signatureC })
        {
            await signatureStore.UpsertAsync(item);
        }

        var signatureFilter = new SignatureListFilter(prefix);
        var signatureSort = new ListSort<SignatureSortColumn>(SignatureSortColumn.Name, SortDirection.Asc);
        var signaturePage1 = await signatureStore.ListPageAsync(beforeId: null, pageSize: 2, filter: signatureFilter, sort: signatureSort);
        var signaturePage2 = await signatureStore.ListPageAsync(signaturePage1.NextCursor, pageSize: 2, filter: signatureFilter, sort: signatureSort);
        Assert.Equal([signatureA.Id, signatureB.Id], signaturePage1.Items.Select(s => s.Id));
        Assert.Equal(signatureC.Id, Assert.Single(signaturePage2.Items).Id);
        Assert.Equal(3, signaturePage1.TotalCount);
        Assert.Equal(2, signaturePage2.Preceding);
        var signatureJumpCursor = await signatureStore.GetPageCursorAsync(2, 2, signatureFilter, signatureSort);
        var signatureJumpPage = await signatureStore.ListPageAsync(signatureJumpCursor, pageSize: 2, filter: signatureFilter, sort: signatureSort);
        Assert.Equal(signaturePage2.Items.Select(s => s.Id), signatureJumpPage.Items.Select(s => s.Id));
        var signatureClampedCursor = await signatureStore.GetPageCursorAsync(99, 2, signatureFilter, signatureSort);
        var signatureClampedPage = await signatureStore.ListPageAsync(signatureClampedCursor, pageSize: 2, filter: signatureFilter, sort: signatureSort);
        Assert.Equal(signaturePage2.Items.Select(s => s.Id), signatureClampedPage.Items.Select(s => s.Id));
    }

    [PostgresFact]
    public async Task Admin_list_boolean_filters_apply_to_all_searchable_stores()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var prefix = $"it-bool-{ViegardId.New():N}";
        var filterText = $"{prefix} alpha OR {prefix} beta -blocked";
        var now = DateTimeOffset.UtcNow;

        var eventStore = new PostgresEventStore(factory, resolver);
        var eventAlpha = Event($"{prefix} alpha", now.AddMinutes(1));
        var eventBeta = Event($"{prefix} beta", now.AddMinutes(2));
        var eventBlocked = Event($"{prefix} beta blocked", now.AddMinutes(3));
        var eventOther = Event($"{prefix} gamma", now.AddMinutes(4));
        foreach (var item in new[] { eventOther, eventBlocked, eventBeta, eventAlpha })
        {
            await eventStore.AddAsync(item);
        }

        var eventMatches = await eventStore.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new EventListFilter(filterText),
            sort: new ListSort<EventSortColumn>(EventSortColumn.Source, SortDirection.Asc));
        Assert.Equal([eventAlpha.Id, eventBeta.Id], eventMatches.Items.Select(e => e.Id));

        var incidentStore = new PostgresIncidentStore(factory);
        var incidentAlpha = Incident($"{prefix} alpha open", now.AddMinutes(1));
        var incidentBeta = Incident($"{prefix} beta open", now.AddMinutes(2));
        var incidentBlocked = Incident($"{prefix} beta blocked", now.AddMinutes(3));
        var incidentClosed = Incident($"{prefix} alpha closed", now.AddMinutes(4)) with { State = IncidentState.Closed };
        foreach (var item in new[] { incidentClosed, incidentBlocked, incidentBeta, incidentAlpha })
        {
            await incidentStore.UpsertAsync(item);
        }

        var incidentMatches = await incidentStore.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new IncidentListFilter(filterText, IncidentState.Open),
            sort: new ListSort<IncidentSortColumn>(IncidentSortColumn.CorrelationKey, SortDirection.Asc));
        Assert.Equal([incidentAlpha.Id, incidentBeta.Id], incidentMatches.Items.Select(i => i.Id));

        var classificationStore = new PostgresClassificationStore(factory, resolver);
        var decisionStore = new PostgresDecisionStore(factory, resolver);
        var classificationAlpha = CreateClassification($"{prefix}-classifier-alpha", "alpha", 1, now.AddMinutes(1));
        var classificationBeta = CreateClassification($"{prefix}-classifier-beta", "beta", 1, now.AddMinutes(2));
        var classificationBlocked = CreateClassification($"{prefix}-classifier-blocked", "blocked", 1, now.AddMinutes(3));
        var classificationDryRun = CreateClassification($"{prefix}-classifier-dry-run", "dry-run", 1, now.AddMinutes(4));
        foreach (var item in new[] { classificationAlpha, classificationBeta, classificationBlocked, classificationDryRun })
        {
            await classificationStore.AddAsync(item);
        }

        var decisionAlpha = Decision(classificationAlpha.Id, $"{prefix}-policy-alpha", DecisionOutcome.Permit, $"{prefix} alpha approved", now.AddMinutes(1));
        var decisionBeta = Decision(classificationBeta.Id, $"{prefix}-policy-beta", DecisionOutcome.Permit, $"{prefix} beta approved", now.AddMinutes(2));
        var decisionBlocked = Decision(classificationBlocked.Id, $"{prefix}-policy-blocked", DecisionOutcome.Permit, $"{prefix} beta blocked", now.AddMinutes(3));
        var decisionDryRun = Decision(classificationDryRun.Id, $"{prefix}-policy-dry-run", DecisionOutcome.DryRun, $"{prefix} alpha dry-run", now.AddMinutes(4));
        foreach (var item in new[] { decisionDryRun, decisionBlocked, decisionBeta, decisionAlpha })
        {
            await decisionStore.AddAsync(item);
        }

        var decisionMatches = await decisionStore.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new DecisionListFilter(filterText, DecisionOutcome.Permit),
            sort: new ListSort<DecisionSortColumn>(DecisionSortColumn.Created, SortDirection.Asc));
        Assert.Equal([decisionAlpha.Id, decisionBeta.Id], decisionMatches.Items.Select(d => d.Id));

        var auditLedger = new PostgresAuditLedger(factory, resolver);
        var auditAlpha = AuditRecord($"{prefix} alpha accepted", null, now.AddMinutes(1));
        var auditBeta = AuditRecord($"{prefix} beta accepted", null, now.AddMinutes(2));
        var auditBlocked = AuditRecord($"{prefix} beta blocked", null, now.AddMinutes(3));
        var auditSystem = AuditRecord($"{prefix} alpha system", null, now.AddMinutes(4)) with { Stage = PipelineStage.System };
        foreach (var item in new[] { auditSystem, auditBlocked, auditBeta, auditAlpha })
        {
            await auditLedger.AppendAsync(item);
        }

        var auditMatches = await auditLedger.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new AuditListFilter(filterText, PipelineStage.Admin),
            sort: new ListSort<AuditSortColumn>(AuditSortColumn.Timestamp, SortDirection.Asc));
        Assert.Equal([auditAlpha.Id, auditBeta.Id], auditMatches.Items.Select(a => a.Id));

        var signatureStore = new PostgresCustomSignatureStore(factory, _dataSource!);
        var signatureAlpha = CustomSignature($"{prefix} alpha");
        var signatureBeta = CustomSignature($"{prefix} beta");
        var signatureBlocked = CustomSignature($"{prefix} beta blocked");
        var signatureOther = CustomSignature($"{prefix} gamma");
        foreach (var item in new[] { signatureOther, signatureBlocked, signatureBeta, signatureAlpha })
        {
            await signatureStore.UpsertAsync(item);
        }

        var signatureMatches = await signatureStore.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new SignatureListFilter(filterText),
            sort: new ListSort<SignatureSortColumn>(SignatureSortColumn.Name, SortDirection.Asc));
        Assert.Equal([signatureAlpha.Id, signatureBeta.Id], signatureMatches.Items.Select(s => s.Id));
    }

    [PostgresFact]
    public async Task Admin_webauthn_credentials_enforce_unique_credential_id_and_ownership()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresAdminUserStore(factory);
        var now = DateTimeOffset.UtcNow;
        var user = new AdminUser
        {
            Id = ViegardId.New(),
            Username = $"webauthn-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordChangedAt = now,
            FailedLoginCount = 0,
            LockedUntil = null,
            MustChangePassword = false,
            TotpEnrolled = true,
            CreatedAt = now,
        };
        await store.CreateAsync(user);

        var credential = new AdminWebAuthnCredential
        {
            Id = ViegardId.New(),
            UserId = user.Id,
            CredentialId = [9, 8, 7, 6],
            PublicKey = [1, 2, 3, 4],
            SignCount = 1,
            Aaguid = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Transports = "[\"usb\"]",
            Name = "postgres key",
            CreatedAt = now,
            LastUsedAt = null,
        };

        Assert.True(await store.AddWebAuthnCredentialAsync(credential));
        Assert.False(await store.AddWebAuthnCredentialAsync(credential with { Id = ViegardId.New() }));

        var listed = Assert.Single(await store.ListWebAuthnCredentialsAsync(user.Id));
        Assert.Equal(credential.CredentialId, listed.CredentialId);

        var found = await store.GetWebAuthnCredentialByCredentialIdAsync([9, 8, 7, 6]);
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.UserId);

        Assert.False(await store.DeleteWebAuthnCredentialAsync(ViegardId.New(), credential.Id));
        Assert.True(await store.DeleteWebAuthnCredentialAsync(user.Id, credential.Id));
    }

    [PostgresFact]
    public async Task Repeated_descriptor_strings_share_one_reference_row()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var store = new PostgresEventStore(factory, resolver);
        var sourceKey = $"it:dedup-{Guid.NewGuid():N}";

        for (var i = 0; i < 3; i++)
        {
            await store.AddAsync(new NormalizedEvent
            {
                Id = Guid.NewGuid(),
                SourceId = sourceKey,
                SourceType = "syslog",
                OccurredAt = DateTimeOffset.UtcNow,
                Entities = [],
                Payload = new HttpRequestEvent { RemoteAddress = "203.0.113.7" },
                RawObservationId = Guid.NewGuid(),
            });
        }

        // A second resolver (fresh cache, as another process would have)
        // must find the same row rather than create a duplicate.
        var secondResolver = new ReferenceResolver(factory);
        var firstId = await resolver.ResolveSourceAsync(sourceKey, "syslog");
        var secondId = await secondResolver.ResolveSourceAsync(sourceKey, "syslog");
        Assert.Equal(firstId, secondId);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Sources.CountAsync(s => s.SourceKey == sourceKey));
    }

    [PostgresFact]
    public async Task Audit_first_source_type_is_filled_by_later_typed_writer()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var sourceKey = $"it:fill-{Guid.NewGuid():N}";

        // Audit path does not know the type; the row starts untyped.
        var id = await resolver.ResolveSourceAsync(sourceKey, sourceType: null);
        var (_, typeBefore) = await resolver.GetSourceAsync(id);
        Assert.Null(typeBefore);

        // First typed writer fills it exactly once.
        var sameId = await resolver.ResolveSourceAsync(sourceKey, "imap");
        Assert.Equal(id, sameId);
        var (_, typeAfter) = await resolver.GetSourceAsync(id);
        Assert.Equal("imap", typeAfter);
    }

    [PostgresFact]
    public async Task Custom_signature_store_round_trips_and_deletes()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresCustomSignatureStore(factory, _dataSource!);
        var signature = CustomSignature($"it-roundtrip-{ViegardId.New():N}");

        var saved = await store.UpsertAsync(signature);
        var restored = await store.GetAsync(saved.Id);

        Assert.NotNull(restored);
        Assert.Equal(signature.Name, restored.Name);
        Assert.Equal(1, restored.Version);

        var updated = await store.UpsertAsync(saved with { Pattern = "second", UpdatedBy = "it" });
        Assert.Equal(2, updated.Version);
        Assert.Equal("second", (await store.GetAsync(updated.Id))!.Pattern);

        var deleted = await store.DeleteAsync(updated.Id);
        Assert.NotNull(deleted);
        Assert.Null(await store.GetAsync(updated.Id));
    }

    [PostgresFact]
    public async Task Custom_signature_contains_all_round_trips_lists_and_refreshes()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresCustomSignatureStore(factory, _dataSource!);
        var token = ViegardId.New().ToString("N");
        var signature = CustomSignature($"it-contains-all-{ViegardId.New():N}") with
        {
            MatchType = CustomSignatureMatchType.ContainsAll,
            Pattern = $"marker={token}",
            AdditionalPatterns = [$"campaign={token}", $"path={token}"],
        };

        var saved = await store.UpsertAsync(signature);
        var listed = await store.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new SignatureListFilter(saved.Name),
            sort: new ListSort<SignatureSortColumn>(SignatureSortColumn.Name, SortDirection.Asc));
        var restored = Assert.Single(listed.Items);

        Assert.Equal(CustomSignatureMatchType.ContainsAll, restored.MatchType);
        Assert.Equal([$"campaign={token}", $"path={token}"], restored.AdditionalPatterns);

        var source = new CustomSignatureRuleSource(store, new DetectionOptions());
        await source.RefreshAsync();

        Assert.Single(source.Evaluate(HttpEvent($"/track?marker={token}&campaign={token}&path={token}")));
        Assert.Empty(source.Evaluate(HttpEvent($"/track?marker={token}&campaign={token}")));
    }

    [PostgresFact]
    public async Task Custom_signature_refresh_skips_unparseable_additional_patterns_json()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var logger = new RecordingLogger<PostgresCustomSignatureStore>();
        var store = new PostgresCustomSignatureStore(factory, _dataSource!, logger);
        var token = ViegardId.New().ToString("N");
        var valid = CustomSignature($"it-valid-json-{ViegardId.New():N}") with
        {
            Target = CustomSignatureTarget.HttpUri,
            Pattern = $"/valid-{token}",
        };
        await store.UpsertAsync(valid);

        await using (var db = factory.CreateDbContext())
        {
            db.CustomSignatures.Add(new CustomSignatureRow
            {
                Id = ViegardId.New(),
                Name = $"it-invalid-json-{ViegardId.New():N}",
                Enabled = true,
                Target = (int)CustomSignatureTarget.HttpUri,
                MatchType = (int)CustomSignatureMatchType.ContainsAll,
                Pattern = $"/invalid-{token}",
                AdditionalPatternsJson = "[not-json",
                Category = "test",
                Severity = 3,
                EvidenceWeight = 1.0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "it",
                Version = 1,
            });
            await db.SaveChangesAsync();
        }

        var source = new CustomSignatureRuleSource(store, new DetectionOptions());
        var exception = await Record.ExceptionAsync(() => source.RefreshAsync());

        Assert.Null(exception);
        Assert.Single(source.Evaluate(HttpEvent($"/valid-{token}")));
        Assert.Empty(source.Evaluate(HttpEvent($"/invalid-{token}")));
        Assert.Contains(logger.Messages, message => message.Contains("Skipping custom signature", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Custom_signature_store_notify_wakes_waiter()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var listener = new PostgresCustomSignatureStore(factory, _dataSource!);
        var writer = new PostgresCustomSignatureStore(factory, _dataSource!);
        var wait = listener.WaitForChangeAsync(
            listener.CurrentChangeVersion,
            TimeSpan.FromSeconds(10),
            CancellationToken.None).AsTask();

        await Task.Delay(300);
        await writer.UpsertAsync(CustomSignature($"it-notify-{ViegardId.New():N}"));

        var version = await wait.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(version > 0);
    }

    [PostgresFact]
    public async Task Ingestion_filter_store_round_trips_notifies_and_refreshes()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var listener = new PostgresIngestionFilterStore(factory, _dataSource!);
        var writer = new PostgresIngestionFilterStore(factory, _dataSource!);
        var wait = listener.WaitForChangeAsync(
            listener.CurrentChangeVersion,
            TimeSpan.FromSeconds(10),
            CancellationToken.None).AsTask();

        await Task.Delay(300);
        var save = await writer.SaveMatrixAsync(
            MDaemonIngestionFilterPolicy.SourceType,
            IngestionMatrix((MDaemonEventKind.Other, true)),
            MDaemonIngestionFilterPolicy.LockedEventKindNames,
            "postgres-it",
            DateTimeOffset.UtcNow);

        var version = await wait.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(version > 0);
        Assert.Empty(save.Before);
        var saved = Assert.Single(save.After, filter => filter.EventKind == MDaemonEventKind.Other.ToString());
        Assert.True(saved.Suppressed);

        var listed = await listener.ListForSourceAsync(MDaemonIngestionFilterPolicy.SourceType);
        Assert.True(listed.Single(filter => filter.EventKind == MDaemonEventKind.Other.ToString()).Suppressed);

        var source = new IngestionFilterSource(listener);
        await source.RefreshAsync();

        Assert.False(source.ShouldEmit(MDaemonEvent(MDaemonEventKind.Other)));
        Assert.True(source.ShouldEmit(MDaemonEvent(MDaemonEventKind.ConnectionAccepted)));
    }

    [PostgresFact]
    public async Task Ingestion_filter_refresh_skips_invalid_rows()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var logger = new RecordingDiagnostics();
        var store = new PostgresIngestionFilterStore(factory, _dataSource!);
        await store.SaveMatrixAsync(
            MDaemonIngestionFilterPolicy.SourceType,
            IngestionMatrix((MDaemonEventKind.Other, true)),
            MDaemonIngestionFilterPolicy.LockedEventKindNames,
            "postgres-it",
            DateTimeOffset.UtcNow);

        await using (var db = factory.CreateDbContext())
        {
            db.IngestionFilters.Add(new IngestionFilterRow
            {
                SourceType = MDaemonIngestionFilterPolicy.SourceType,
                EventKind = "NotARealKind",
                Suppressed = true,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "postgres-it",
            });
            await db.SaveChangesAsync();
        }

        var source = new IngestionFilterSource(store, logger);
        await source.RefreshAsync();

        Assert.Single(logger.InvalidFilters);
        Assert.False(source.ShouldEmit(MDaemonEvent(MDaemonEventKind.Other)));
        Assert.True(source.ShouldEmit(MDaemonEvent(MDaemonEventKind.ConnectionAccepted)));
    }

    [PostgresFact]
    public async Task Retention_settings_store_round_trips_and_detects_optimistic_concurrency_conflict()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresRetentionSettingsStore(factory);
        var now = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);

        var saved = await store.UpsertAsync(
            RetentionSettingsWith((RetentionTarget.Events, 90), (RetentionTarget.AuditRecords, 365)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: now);

        Assert.True(saved.Succeeded);
        Assert.Equal(1, saved.Settings!.Version);
        Assert.Equal(90, saved.Settings.EventsDays);
        Assert.Equal(365, saved.Settings.AuditRecordsDays);
        Assert.Equal("hannah", saved.Settings.UpdatedBy);

        var updated = await store.UpsertAsync(
            saved.Settings with { EventsDays = 120 },
            expectedVersion: saved.Settings.Version,
            updatedBy: "operator",
            updatedAt: now.AddMinutes(1));

        Assert.True(updated.Succeeded);
        Assert.Equal(2, updated.Settings!.Version);
        Assert.Equal(120, updated.Settings.EventsDays);

        var conflict = await store.UpsertAsync(
            updated.Settings with { EventsDays = 7 },
            expectedVersion: saved.Settings.Version,
            updatedBy: "stale",
            updatedAt: now.AddMinutes(2));

        Assert.False(conflict.Succeeded);
        Assert.Equal(2, conflict.Settings!.Version);
        Assert.Equal(120, conflict.Settings.EventsDays);

        var counts = RetentionSettings.EmptyCounts().ToDictionary(pair => pair.Key, pair => pair.Value);
        counts[RetentionTarget.Events] = 3;
        await store.UpdateLastCycleAsync(now.AddHours(1), counts);
        var afterCycle = await store.GetAsync();
        Assert.Equal(2, afterCycle!.Version);
        Assert.Equal(3, afterCycle.LastCycleCounts()[RetentionTarget.Events]);
        Assert.Equal(now.AddHours(1), afterCycle.LastCycleAt);
    }

    [PostgresFact]
    public async Task Retention_settings_seed_is_create_only_and_race_safe()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var firstStore = new PostgresRetentionSettingsStore(factory);
        var secondStore = new PostgresRetentionSettingsStore(factory);
        var seededAt = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);

        var seeded = await firstStore.SeedIfMissingAsync(
            new RetentionOptions
            {
                EventsDays = 90,
                AuditRecordsDays = 365,
            },
            seededAt);

        Assert.Equal(90, seeded!.EventsDays);
        Assert.Equal(365, seeded.AuditRecordsDays);
        Assert.Equal(seededAt, seeded.SeededAt);

        var second = await secondStore.SeedIfMissingAsync(
            new RetentionOptions
            {
                EventsDays = 7,
                AuditRecordsDays = null,
            },
            seededAt.AddMinutes(1));

        Assert.Equal(90, second!.EventsDays);
        Assert.Equal(365, second.AuditRecordsDays);
        Assert.Equal(seededAt, second.SeededAt);

        await ClearRetentionSettingsAsync(factory);
        var admin = await firstStore.UpsertAsync(
            RetentionSettingsWith((RetentionTarget.Events, 45)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: seededAt.AddMinutes(2));
        Assert.True(admin.Succeeded);

        var afterAdminFirst = await secondStore.SeedIfMissingAsync(
            new RetentionOptions { EventsDays = 90 },
            seededAt.AddMinutes(3));
        Assert.Equal(45, afterAdminFirst!.EventsDays);
        Assert.Null(afterAdminFirst.SeededAt);

        await ClearRetentionSettingsAsync(factory);
        var seedA = firstStore.SeedIfMissingAsync(
            new RetentionOptions { EventsDays = 11 },
            seededAt.AddMinutes(4)).AsTask();
        var seedB = secondStore.SeedIfMissingAsync(
            new RetentionOptions { EventsDays = 22 },
            seededAt.AddMinutes(5)).AsTask();
        await Task.WhenAll(seedA, seedB);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.RetentionSettings.CountAsync());
        var raced = await firstStore.GetAsync();
        Assert.NotNull(raced);
        Assert.Contains(raced.EventsDays, new int?[] { 11, 22 });
        Assert.Equal(1, raced.Version);
    }

    [PostgresFact]
    public async Task Policy_threshold_settings_store_creates_updates_conflicts_and_notifies()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var listener = new PostgresPolicyThresholdSettingsStore(factory, _dataSource!);
        var writer = new PostgresPolicyThresholdSettingsStore(factory, _dataSource!);
        var now = new DateTimeOffset(2026, 9, 16, 14, 30, 0, TimeSpan.Zero);
        var wait = listener.WaitForChangeAsync(
            listener.CurrentChangeVersion,
            TimeSpan.FromSeconds(10),
            CancellationToken.None).AsTask();

        await Task.Delay(300);
        var saved = await writer.UpdateAsync(
            PolicyThresholdSettingsWith(reviewConfidence: 0.6, actionConfidence: 0.9, actionMinSeverity: 7),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: now);

        var version = await wait.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(version > 0);
        Assert.True(saved.Succeeded);
        var savedSettings = saved.Settings!;
        Assert.Equal(1, savedSettings.RowVersion);
        Assert.Equal(0.6, savedSettings.ReviewConfidence);
        Assert.Equal("hannah", savedSettings.UpdatedBy);

        var updated = await writer.UpdateAsync(
            savedSettings with
            {
                ReviewConfidence = 0.7,
                ActionConfidence = 0.95,
                ActionMinSeverity = 8,
            },
            expectedRowVersion: savedSettings.RowVersion,
            updatedBy: "operator",
            updatedAt: now.AddMinutes(1));

        Assert.True(updated.Succeeded);
        var updatedSettings = updated.Settings!;
        Assert.Equal(2, updatedSettings.RowVersion);
        Assert.Equal(0.7, updatedSettings.ReviewConfidence);
        Assert.Equal(0.95, updatedSettings.ActionConfidence);
        Assert.Equal(8, updatedSettings.ActionMinSeverity);

        var conflict = await writer.UpdateAsync(
            updatedSettings with { ReviewConfidence = 0.5 },
            expectedRowVersion: savedSettings.RowVersion,
            updatedBy: "stale",
            updatedAt: now.AddMinutes(2));

        Assert.False(conflict.Succeeded);
        Assert.Equal(2, conflict.Settings!.RowVersion);
        Assert.Equal(0.7, conflict.Settings.ReviewConfidence);
    }

    [PostgresFact]
    public async Task Policy_threshold_settings_seed_is_create_only_and_race_safe()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var firstStore = new PostgresPolicyThresholdSettingsStore(factory, _dataSource!);
        var secondStore = new PostgresPolicyThresholdSettingsStore(factory, _dataSource!);
        var seededAt = new DateTimeOffset(2026, 9, 16, 14, 30, 0, TimeSpan.Zero);

        var seeded = await firstStore.TryCreateAsync(
            PolicyThresholdSettings.FromOptions(
                new PolicyOptions
                {
                    AiReviewConfidence = 0.6,
                    AiActionConfidence = 0.9,
                    AiActionMinSeverity = 7,
                },
                seededAt));

        Assert.True(seeded.Created);
        Assert.Equal(0.6, seeded.Settings.ReviewConfidence);
        Assert.Equal(0.9, seeded.Settings.ActionConfidence);
        Assert.Equal(7, seeded.Settings.ActionMinSeverity);
        Assert.Equal(seededAt, seeded.Settings.UpdatedAt);

        var second = await secondStore.TryCreateAsync(
            PolicyThresholdSettings.FromOptions(
                new PolicyOptions
                {
                    AiReviewConfidence = 0.3,
                    AiActionConfidence = 0.8,
                    AiActionMinSeverity = 4,
                },
                seededAt.AddMinutes(1)));

        Assert.False(second.Created);
        Assert.Equal(0.6, second.Settings.ReviewConfidence);
        Assert.Equal(seededAt, second.Settings.UpdatedAt);

        await ClearPolicyThresholdSettingsAsync(factory);
        var admin = await firstStore.UpdateAsync(
            PolicyThresholdSettingsWith(reviewConfidence: 0.55, actionConfidence: 0.85, actionMinSeverity: 6),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: seededAt.AddMinutes(2));
        Assert.True(admin.Succeeded);

        var afterAdminFirst = await secondStore.TryCreateAsync(
            PolicyThresholdSettings.FromOptions(new PolicyOptions(), seededAt.AddMinutes(3)));
        Assert.False(afterAdminFirst.Created);
        Assert.Equal(0.55, afterAdminFirst.Settings.ReviewConfidence);
        Assert.Equal("hannah", afterAdminFirst.Settings.UpdatedBy);

        await ClearPolicyThresholdSettingsAsync(factory);
        var seedA = firstStore.TryCreateAsync(
            PolicyThresholdSettings.FromOptions(
                new PolicyOptions { AiReviewConfidence = 0.11, AiActionConfidence = 0.8, AiActionMinSeverity = 4 },
                seededAt.AddMinutes(4))).AsTask();
        var seedB = secondStore.TryCreateAsync(
            PolicyThresholdSettings.FromOptions(
                new PolicyOptions { AiReviewConfidence = 0.22, AiActionConfidence = 0.8, AiActionMinSeverity = 5 },
                seededAt.AddMinutes(5))).AsTask();
        await Task.WhenAll(seedA, seedB);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.PolicyThresholdSettings.CountAsync());
        var raced = await firstStore.GetAsync();
        Assert.NotNull(raced);
        Assert.Contains(raced!.ReviewConfidence, new[] { 0.11, 0.22 });
        Assert.Equal(1, raced.RowVersion);
    }

    [PostgresFact]
    public async Task MikroTik_router_store_crud_enforces_uniqueness_concurrency_and_credentials()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresMikroTikRouterStore(factory);
        var now = new DateTimeOffset(2026, 9, 16, 19, 30, 0, TimeSpan.Zero);
        var first = MikroTikRouter("router-a", now);
        var second = MikroTikRouter("router-b", now.AddMinutes(1));

        var created = await store.CreateAsync(first, "cipher-1");
        Assert.True(created.Succeeded);
        Assert.Equal(1, created.Router!.RowVersion);
        Assert.Equal("cipher-1", await store.GetCredentialCiphertextAsync(first.Id));

        var duplicate = await store.CreateAsync(second with { Name = "router-a" }, "cipher-dup");
        Assert.Equal(MikroTikRouterSaveStatus.DuplicateName, duplicate.Status);

        var updated = await store.UpdateAsync(
            created.Router with
            {
                BaseUrl = "http://router-a.example.com:8080",
                UpdatedAt = now.AddMinutes(2),
                UpdatedBy = "operator",
            },
            expectedRowVersion: created.Router.RowVersion);
        Assert.True(updated.Succeeded);
        Assert.Equal(2, updated.Router!.RowVersion);
        Assert.Equal("cipher-1", await store.GetCredentialCiphertextAsync(first.Id));

        var rotated = await store.UpdateAsync(
            updated.Router with
            {
                Username = "viegard2",
                UpdatedAt = now.AddMinutes(3),
                UpdatedBy = "operator",
            },
            expectedRowVersion: updated.Router.RowVersion,
            passwordCiphertext: "cipher-2");
        Assert.True(rotated.Succeeded);
        Assert.Equal("cipher-2", await store.GetCredentialCiphertextAsync(first.Id));

        var conflict = await store.UpdateAsync(
            updated.Router with { Username = "stale", UpdatedAt = now.AddMinutes(4) },
            expectedRowVersion: updated.Router.RowVersion);
        Assert.Equal(MikroTikRouterSaveStatus.Conflict, conflict.Status);
        Assert.Equal(3, conflict.Router!.RowVersion);

        var secondCreated = await store.CreateAsync(second, "cipher-3");
        Assert.True(secondCreated.Succeeded);
        var nameConflict = await store.UpdateAsync(
            secondCreated.Router! with { Name = "router-a", UpdatedAt = now.AddMinutes(5) },
            expectedRowVersion: secondCreated.Router.RowVersion);
        Assert.Equal(MikroTikRouterSaveStatus.DuplicateName, nameConflict.Status);

        var deleteConflict = await store.DeleteAsync(first.Id, expectedRowVersion: 1);
        Assert.Equal(MikroTikRouterDeleteStatus.Conflict, deleteConflict.Status);

        var deleted = await store.DeleteAsync(first.Id, rotated.Router!.RowVersion);
        Assert.True(deleted.Succeeded);
        Assert.Null(await store.GetAsync(first.Id));
        Assert.Null(await store.GetCredentialCiphertextAsync(first.Id));
    }

    [PostgresFact]
    public async Task Active_ban_store_replaces_removes_lists_and_cleans_up()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresActiveBanStore(factory);
        var now = new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.Zero);
        var first = ActiveBanRecord("2001:db8:0:0::1", now, now.AddMinutes(5));
        var replacement = ActiveBanRecord("2001:db8::1", now.AddMinutes(1), now.AddMinutes(10));
        var expired = ActiveBanRecord("203.0.113.10", now.AddMinutes(-10), now.AddSeconds(-1));
        var live = ActiveBanRecord("203.0.113.11", now.AddMinutes(-1), now.AddMinutes(5));

        await store.UpsertByIpAsync(first);
        var saved = await store.UpsertByIpAsync(replacement);
        await store.UpsertByIpAsync(expired);
        await store.UpsertByIpAsync(live);

        Assert.Equal("2001:db8::1", saved.Ip);
        Assert.Equal(replacement.Id, (await store.GetByIpAsync("2001:db8:0:0:0:0:0:1"))!.Id);
        Assert.Equal(
            ["2001:db8::1", "203.0.113.11"],
            (await store.ListUnexpiredAsync(now)).Select(ban => ban.Ip).ToArray());

        Assert.Equal(1, await store.DeleteExpiredAsync(now));
        Assert.Null(await store.GetByIpAsync("203.0.113.10"));
        Assert.True(await store.RemoveByIpAsync("203.0.113.11"));
        Assert.False(await store.RemoveByIpAsync("203.0.113.11"));
    }

    [PostgresFact]
    public async Task Action_store_round_trips_results_json()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var store = new PostgresActionStore(factory, resolver);
        var action = new ActionRecord
        {
            Id = ViegardId.New(),
            DecisionId = ViegardId.New(),
            ProviderId = "mikrotik",
            OperationId = "ban-ip",
            ParametersJson = """{"ip":"203.0.113.10","timeout":"5m"}""",
            RollbackJson = """{"ip":"203.0.113.10"}""",
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

    [PostgresFact]
    public async Task Action_store_lists_recent_by_provider_in_requested_order()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var store = new PostgresActionStore(factory, resolver);
        var now = DateTimeOffset.UtcNow;
        var first = Action("mikrotik", "ban-ip", now.AddMinutes(-2));
        var second = Action("other", "ban-ip", now.AddMinutes(-1));
        var third = Action("mikrotik", "remove-ban", now);
        await store.AddAsync(first);
        await store.AddAsync(second);
        await store.AddAsync(third);

        var recent = await store.ListRecentByProviderAsync("mikrotik", limit: 1);

        Assert.Equal(third.Id, Assert.Single(recent).Id);
        Assert.Empty(await store.ListRecentByProviderAsync("missing-provider", limit: 10));
    }

    [PostgresFact]
    public async Task Decision_store_try_review_updates_only_unreviewed_requireapproval()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var store = new PostgresDecisionStore(factory, resolver);
        var now = DateTimeOffset.UtcNow;
        var permit = Decision(ViegardId.New(), $"it-review-{ViegardId.New():N}-permit", DecisionOutcome.Permit, "review", now);
        var pending = Decision(ViegardId.New(), $"it-review-{ViegardId.New():N}-pending", DecisionOutcome.RequireApproval, "review", now);
        var alreadyReviewed = Decision(ViegardId.New(), $"it-review-{ViegardId.New():N}-reviewed", DecisionOutcome.RequireApproval, "review", now) with
        {
            ReviewedBy = "hannah",
            ReviewedAt = now.AddMinutes(-1),
            ReviewOutcome = DecisionReviewOutcome.Approved,
        };
        await store.AddAsync(permit);
        await store.AddAsync(pending);
        await store.AddAsync(alreadyReviewed);

        Assert.Null(await store.TryReviewAsync(permit.Id, DecisionReviewOutcome.Approved, "operator", now));
        Assert.Null(await store.TryReviewAsync(alreadyReviewed.Id, DecisionReviewOutcome.Rejected, "operator", now));

        var reviewed = await store.TryReviewAsync(pending.Id, DecisionReviewOutcome.Rejected, "operator", now);

        Assert.NotNull(reviewed);
        Assert.Equal(DecisionReviewOutcome.Rejected, reviewed!.ReviewOutcome);
        Assert.Equal("operator", reviewed.ReviewedBy);
        Assert.NotNull(reviewed.ReviewedAt);
        Assert.InRange(
            (reviewed.ReviewedAt.Value - now.ToUniversalTime()).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
    }

    [PostgresFact]
    public async Task Decision_store_try_review_allows_only_one_concurrent_winner()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var resolver = new ReferenceResolver(factory);
        var store = new PostgresDecisionStore(factory, resolver);
        var decision = Decision(
            ViegardId.New(),
            $"it-review-race-{ViegardId.New():N}",
            DecisionOutcome.RequireApproval,
            "review race",
            DateTimeOffset.UtcNow);
        await store.AddAsync(decision);

        var attempts = Enumerable.Range(0, 12)
            .Select(index => Task.Run(async () =>
                await store.TryReviewAsync(
                    decision.Id,
                    index % 2 == 0 ? DecisionReviewOutcome.Approved : DecisionReviewOutcome.Rejected,
                    $"operator-{index}",
                    DateTimeOffset.UtcNow)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result is not null);
        var saved = await store.GetAsync(decision.Id);
        Assert.NotNull(saved!.ReviewedAt);
        Assert.NotNull(saved.ReviewOutcome);
    }

    [PostgresFact]
    public async Task Retention_store_purges_only_eligible_rows()
    {
        var factory = new TestDbContextFactory(_dataSource!);
        var store = new PostgresRetentionStore(factory);
        var now = DateTimeOffset.UtcNow;
        var prefix = $"it-retention-{ViegardId.New():N}";

        await using var db = factory.CreateDbContext();
        var source = new SourceRow
        {
            SourceKey = $"{prefix}-source",
            SourceType = "syslog",
            FirstSeenAt = now,
        };
        var classifier = new ClassifierRow
        {
            ClassifierKey = $"{prefix}-classifier",
            FirstSeenAt = now,
        };
        var policy = new PolicyRow
        {
            PolicyKey = $"{prefix}-policy",
            PolicyVersion = "1",
            FirstSeenAt = now,
        };
        var provider = new ActionProviderRow
        {
            ProviderKey = $"{prefix}-provider",
            FirstSeenAt = now,
        };
        var user = new AdminUserRow
        {
            Id = ViegardId.New(),
            Username = $"{prefix}-admin",
            PasswordHash = "hash",
            PasswordChangedAt = now,
            FailedLoginCount = 0,
            LockedUntil = null,
            MustChangePassword = false,
            TotpEnrolled = true,
            CreatedAt = now,
        };
        db.AddRange(source, classifier, policy, provider, user);
        await db.SaveChangesAsync();

        var oldRaw = RawObservation(source.Id, now.AddDays(-31), $"{prefix}/raw-old");
        var newRaw = RawObservation(source.Id, now.AddDays(-29), $"{prefix}/raw-new");
        var oldEventA = EventRow(source.Id, now.AddDays(-91), ViegardId.New());
        var oldEventB = EventRow(source.Id, now.AddDays(-92), ViegardId.New());
        var newEvent = EventRow(source.Id, now.AddDays(-89), ViegardId.New());
        var oldOpenIncident = IncidentRow($"{prefix}-old-open", now.AddDays(-181), IncidentState.Open);
        var oldClosedIncident = IncidentRow($"{prefix}-old-closed", now.AddDays(-181), IncidentState.Closed);
        var newClosedIncident = IncidentRow($"{prefix}-new-closed", now.AddDays(-179), IncidentState.Closed);
        var oldClassification = ClassificationRow(classifier.Id, now.AddDays(-181));
        var newClassification = ClassificationRow(classifier.Id, now.AddDays(-179));
        var oldDecision = DecisionRow(policy.Id, oldClassification.Id, now.AddDays(-181));
        var newDecision = DecisionRow(policy.Id, newClassification.Id, now.AddDays(-179));
        var oldAction = ActionRow(provider.Id, oldDecision.Id, now.AddDays(-181));
        var newAction = ActionRow(provider.Id, newDecision.Id, now.AddDays(-179));
        var oldAudit = AuditRecordRow(now.AddDays(-366), $"{prefix}-old audit");
        var newAudit = AuditRecordRow(now.AddDays(-364), $"{prefix}-new audit");
        var correction = new CorrectionRow
        {
            Id = ViegardId.New(),
            ClassificationId = oldClassification.Id,
            CorrectedCategory = "ham",
            CorrectedBy = "integration",
            Note = prefix,
            CreatedAt = now.AddDays(-365),
        };
        var oldDeadLetter = QueueMessage($"{prefix}-dead-old", now.AddDays(-31), deadLettered: true);
        var newDeadLetter = QueueMessage($"{prefix}-dead-new", now.AddDays(-29), deadLettered: true);
        var liveQueueMessage = QueueMessage($"{prefix}-live-old", now.AddDays(-31), deadLettered: false);
        var oldRevokedSession = AdminSession(user.Id, now.AddDays(-60), now.AddDays(60), now.AddDays(-31));
        var newRevokedSession = AdminSession(user.Id, now.AddDays(-60), now.AddDays(60), now.AddDays(-29));
        var oldExpiredSession = AdminSession(user.Id, now.AddDays(-60), now.AddDays(-31), revokedAt: null);
        var newExpiredSession = AdminSession(user.Id, now.AddDays(-60), now.AddDays(-29), revokedAt: null);
        var liveSession = AdminSession(user.Id, now, now.AddDays(1), revokedAt: null);

        db.AddRange(
            oldRaw,
            newRaw,
            oldEventA,
            oldEventB,
            newEvent,
            oldOpenIncident,
            oldClosedIncident,
            newClosedIncident,
            oldClassification,
            newClassification,
            oldDecision,
            newDecision,
            oldAction,
            newAction,
            oldAudit,
            newAudit,
            correction,
            oldDeadLetter,
            newDeadLetter,
            liveQueueMessage,
            oldRevokedSession,
            newRevokedSession,
            oldExpiredSession,
            newExpiredSession,
            liveSession);
        await db.SaveChangesAsync();

        Assert.Equal(1, await store.PurgeAsync(RetentionTarget.RawObservations, now.AddDays(-30), batchSize: 1));
        Assert.Equal(2, await store.PurgeAsync(RetentionTarget.Events, now.AddDays(-90), batchSize: 1));
        Assert.Equal(1, await store.PurgeAsync(RetentionTarget.Incidents, now.AddDays(-180), batchSize: 1));
        Assert.Equal(1, await store.PurgeAsync(RetentionTarget.Classifications, now.AddDays(-180), batchSize: 1));
        Assert.Equal(1, await store.PurgeAsync(RetentionTarget.Decisions, now.AddDays(-180), batchSize: 1));
        Assert.Equal(1, await store.PurgeAsync(RetentionTarget.Actions, now.AddDays(-180), batchSize: 1));
        Assert.Equal(1, await store.PurgeAsync(RetentionTarget.AuditRecords, now.AddDays(-365), batchSize: 1));
        Assert.Equal(1, await store.PurgeAsync(RetentionTarget.DeadLetteredQueueMessages, now.AddDays(-30), batchSize: 1));
        Assert.Equal(2, await store.PurgeAsync(RetentionTarget.ExpiredAdminSessions, now.AddDays(-30), batchSize: 1));

        db.ChangeTracker.Clear();
        Assert.False(await db.RawObservations.AnyAsync(r => r.Id == oldRaw.Id));
        Assert.True(await db.RawObservations.AnyAsync(r => r.Id == newRaw.Id));
        Assert.False(await db.Events.AnyAsync(e => e.Id == oldEventA.Id || e.Id == oldEventB.Id));
        Assert.True(await db.Events.AnyAsync(e => e.Id == newEvent.Id));
        Assert.True(await db.Incidents.AnyAsync(i => i.Id == oldOpenIncident.Id));
        Assert.False(await db.Incidents.AnyAsync(i => i.Id == oldClosedIncident.Id));
        Assert.True(await db.Incidents.AnyAsync(i => i.Id == newClosedIncident.Id));
        Assert.False(await db.Classifications.AnyAsync(c => c.Id == oldClassification.Id));
        Assert.True(await db.Classifications.AnyAsync(c => c.Id == newClassification.Id));
        Assert.False(await db.Decisions.AnyAsync(d => d.Id == oldDecision.Id));
        Assert.True(await db.Decisions.AnyAsync(d => d.Id == newDecision.Id));
        Assert.False(await db.Actions.AnyAsync(a => a.Id == oldAction.Id));
        Assert.True(await db.Actions.AnyAsync(a => a.Id == newAction.Id));
        Assert.False(await db.AuditRecords.AnyAsync(a => a.Id == oldAudit.Id));
        Assert.True(await db.AuditRecords.AnyAsync(a => a.Id == newAudit.Id));
        Assert.True(await db.Corrections.AnyAsync(c => c.Id == correction.Id));
        Assert.False(await db.QueueMessages.AnyAsync(q => q.Id == oldDeadLetter.Id));
        Assert.True(await db.QueueMessages.AnyAsync(q => q.Id == newDeadLetter.Id));
        Assert.True(await db.QueueMessages.AnyAsync(q => q.Id == liveQueueMessage.Id));
        Assert.False(await db.AdminSessions.AnyAsync(s => s.Id == oldRevokedSession.Id));
        Assert.True(await db.AdminSessions.AnyAsync(s => s.Id == newRevokedSession.Id));
        Assert.False(await db.AdminSessions.AnyAsync(s => s.Id == oldExpiredSession.Id));
        Assert.True(await db.AdminSessions.AnyAsync(s => s.Id == newExpiredSession.Id));
        Assert.True(await db.AdminSessions.AnyAsync(s => s.Id == liveSession.Id));
    }

    private static CustomSignature CustomSignature(string name) => new()
    {
        Id = ViegardId.New(),
        Name = name,
        Enabled = true,
        Target = CustomSignatureTarget.HttpQuery,
        MatchType = CustomSignatureMatchType.Contains,
        Pattern = "ref=aftership",
        Category = "referral-bot",
        Severity = 3,
        EvidenceWeight = 1.0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "it",
        Version = 0,
    };

    private static IReadOnlyDictionary<string, bool> IngestionMatrix(params (MDaemonEventKind Kind, bool Suppressed)[] overrides)
    {
        var matrix = MDaemonIngestionFilterPolicy.Descriptors.ToDictionary(
            descriptor => descriptor.EventKindName,
            _ => false,
            StringComparer.Ordinal);
        foreach (var (kind, suppressed) in overrides)
        {
            matrix[kind.ToString()] = suppressed;
        }

        return matrix;
    }

    private static NormalizedEvent MDaemonEvent(MDaemonEventKind eventKind) => new()
    {
        Id = ViegardId.New(),
        SourceId = "mdaemon:logs",
        SourceType = MDaemonIngestionFilterPolicy.SourceType,
        OccurredAt = DateTimeOffset.UtcNow,
        Entities = [],
        Payload = new MDaemonLogEvent
        {
            LogKind = MDaemonLogKind.SmtpIn,
            EventKind = eventKind,
            Message = "test",
        },
        RawObservationId = ViegardId.New(),
    };

    private static RetentionSettings RetentionSettingsWith(params (RetentionTarget Target, int? Days)[] values)
    {
        var settings = new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "it",
        };
        foreach (var (target, days) in values)
        {
            settings = settings.WithDays(target, days);
        }

        return settings;
    }

    private static async Task ClearRetentionSettingsAsync(TestDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        await db.RetentionSettings.ExecuteDeleteAsync();
    }

    private static PolicyThresholdSettings PolicyThresholdSettingsWith(
        double reviewConfidence,
        double actionConfidence,
        int actionMinSeverity) => new()
        {
            Id = PolicyThresholdSettings.FixedId,
            ReviewConfidence = reviewConfidence,
            ActionConfidence = actionConfidence,
            ActionMinSeverity = actionMinSeverity,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "it",
        };

    private static async Task ClearPolicyThresholdSettingsAsync(TestDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        await db.PolicyThresholdSettings.ExecuteDeleteAsync();
    }

    private static MikroTikRouter MikroTikRouter(string name, DateTimeOffset now) => new()
    {
        Id = ViegardId.New(),
        Name = name,
        BaseUrl = $"http://{name}.example.com",
        TransportMode = MikroTikRouterTransportMode.PlainHttp,
        PinnedCertificateSha256 = null,
        Username = "viegard",
        Enabled = true,
        CreatedAt = now,
        UpdatedAt = now,
        UpdatedBy = "it",
        RowVersion = 0,
    };

    private static ActiveBan ActiveBanRecord(string ip, DateTimeOffset createdAt, DateTimeOffset expiresAt) => new()
    {
        Id = ViegardId.New(),
        Ip = ip,
        CreatedAt = createdAt,
        ExpiresAt = expiresAt,
        DecisionId = ViegardId.New(),
        ActionId = ViegardId.New(),
    };

    private static NormalizedEvent Event(string sourceKey, DateTimeOffset occurredAt) => new()
    {
        Id = ViegardId.New(),
        SourceId = sourceKey,
        SourceType = "syslog",
        OccurredAt = occurredAt,
        Entities = [],
        Payload = new HttpRequestEvent { RemoteAddress = "203.0.113.7", Uri = $"/{sourceKey}" },
        RawObservationId = ViegardId.New(),
    };

    private static NormalizedEvent HttpEvent(string uri) => new()
    {
        Id = ViegardId.New(),
        SourceId = "nginx-test",
        SourceType = "syslog",
        OccurredAt = DateTimeOffset.UtcNow,
        Entities = [],
        Payload = new HttpRequestEvent
        {
            RemoteAddress = "203.0.113.7",
            Method = "GET",
            Uri = uri,
            Protocol = "HTTP/1.1",
            StatusCode = 200,
        },
        RawObservationId = ViegardId.New(),
    };

    private static Incident Incident(string correlationKey, DateTimeOffset windowStart) => new()
    {
        Id = ViegardId.New(),
        CorrelationKey = correlationKey,
        WindowStart = windowStart,
        WindowEnd = windowStart.AddMinutes(1),
        EventIds = [ViegardId.New()],
        Evidence = [],
        State = IncidentState.Open,
    };

    private static Classification CreateClassification(string classifierId, string category, int severity, DateTimeOffset createdAt) => new()
    {
        Id = ViegardId.New(),
        SubjectKind = ClassificationSubjectKind.Incident,
        SubjectId = ViegardId.New(),
        ClassifierId = classifierId,
        Category = category,
        Confidence = 0.9,
        Severity = severity,
        Reasons = ["integration test"],
        CreatedAt = createdAt,
    };

    private static Decision Decision(
        Guid classificationId,
        string policyId,
        DecisionOutcome outcome,
        string prefix,
        DateTimeOffset createdAt) => new()
    {
        Id = ViegardId.New(),
        ClassificationId = classificationId,
        PolicyId = policyId,
        PolicyVersion = "1",
        Outcome = outcome,
        Rationale = $"{prefix} decision rationale",
        Guardrails = [],
        CreatedAt = createdAt,
    };

    private static InstanceRegistration InstanceRegistration(string instanceId, string version, DateTimeOffset now) => new()
    {
        InstanceId = instanceId,
        Version = version,
        CommitSha = BuildVersion.Parse(version).CommitSha,
        Roles = "sources",
        UpgradeTarget = "satellite-a",
        HostName = "host.example.com",
        StartedAt = now.AddHours(-1),
        ReportedAt = now,
    };

    private static AuditRecord AuditRecord(string summary, string? sourceId, DateTimeOffset timestamp) => new()
    {
        Id = ViegardId.New(),
        Timestamp = timestamp,
        Stage = PipelineStage.Admin,
        Summary = summary,
        SourceId = sourceId,
    };

    private static RawObservationRow RawObservation(int sourceId, DateTimeOffset observedAt, string payloadReference) => new()
    {
        Id = ViegardId.New(),
        SourceId = sourceId,
        ObservedAt = observedAt,
        PayloadReference = payloadReference,
        IngestOffset = null,
        RawPayload = "raw",
    };

    private static NormalizedEventRow EventRow(int sourceId, DateTimeOffset occurredAt, Guid rawObservationId) => new()
    {
        Id = ViegardId.New(),
        SourceId = sourceId,
        OccurredAt = occurredAt,
        EntitiesJson = "[]",
        PayloadJson = "{}",
        RawObservationId = rawObservationId,
    };

    private static IncidentRow IncidentRow(string correlationKey, DateTimeOffset windowStart, IncidentState state) => new()
    {
        Id = ViegardId.New(),
        CorrelationKey = correlationKey,
        WindowStart = windowStart,
        WindowEnd = windowStart.AddMinutes(1),
        EventIdsJson = "[]",
        EvidenceJson = "[]",
        State = (int)state,
    };

    private static ClassificationRow ClassificationRow(int classifierId, DateTimeOffset createdAt) => new()
    {
        Id = ViegardId.New(),
        SubjectKind = (int)ClassificationSubjectKind.Incident,
        SubjectId = ViegardId.New(),
        ClassifierId = classifierId,
        Category = "test",
        Confidence = 0.5,
        Severity = 5,
        ReasonsJson = "[]",
        CreatedAt = createdAt,
    };

    private static DecisionRow DecisionRow(int policyId, Guid classificationId, DateTimeOffset createdAt) => new()
    {
        Id = ViegardId.New(),
        ClassificationId = classificationId,
        PolicyId = policyId,
        Outcome = (int)DecisionOutcome.DryRun,
        Rationale = "retention integration",
        GuardrailsJson = "[]",
        CreatedAt = createdAt,
    };

    private static ActionRecordRow ActionRow(int providerId, Guid decisionId, DateTimeOffset requestedAt) => new()
    {
        Id = ViegardId.New(),
        DecisionId = decisionId,
        ProviderId = providerId,
        OperationId = "retention-test",
        Status = (int)ActionStatus.Succeeded,
        RequestedAt = requestedAt,
    };

    private static ActionRecord Action(string providerId, string operationId, DateTimeOffset requestedAt) => new()
    {
        Id = ViegardId.New(),
        DecisionId = ViegardId.New(),
        ProviderId = providerId,
        OperationId = operationId,
        ParametersJson = """{"ip":"203.0.113.10","timeout":"5m"}""",
        Status = ActionStatus.Pending,
        RequestedAt = requestedAt,
    };

    private static AuditRecordRow AuditRecordRow(DateTimeOffset timestamp, string summary) => new()
    {
        Id = ViegardId.New(),
        Timestamp = timestamp,
        Stage = (int)PipelineStage.System,
        Summary = summary,
    };

    private static QueueMessageRow QueueMessage(string queueName, DateTimeOffset enqueuedAt, bool deadLettered) => new()
    {
        QueueName = queueName,
        PayloadJson = "{}",
        DeliveryCount = deadLettered ? 5 : 0,
        EnqueuedAt = enqueuedAt,
        LeasedUntil = null,
        DeadLettered = deadLettered,
    };

    private static AdminSessionRow AdminSession(
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset absoluteExpiresAt,
        DateTimeOffset? revokedAt) => new()
    {
        Id = ViegardId.New(),
        UserId = userId,
        CreatedAt = createdAt,
        LastSeenAt = createdAt,
        AbsoluteExpiresAt = absoluteExpiresAt,
        IdleExpiresAt = absoluteExpiresAt,
        Ip = "127.0.0.1",
        IpBindingMode = "strict",
        UserAgent = "retention-test",
        RevokedAt = revokedAt,
    };

    private static PostgresSatelliteRoleStore CreateSatelliteStore(NpgsqlDataSource dataSource)
    {
        var builder = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString!);
        return new PostgresSatelliteRoleStore(
            dataSource,
            Options.Create(new DatabaseOptions
            {
                Host = builder.Host ?? "127.0.0.1",
                Port = builder.Port,
                Name = builder.Database ?? "viegard_test",
                Username = builder.Username ?? "viegard_test",
                Schema = TestDatabase.Schema,
                PasswordSecretName = "viegard-db-password",
            }));
    }

    private static async Task AssertCanAuthenticateAsync(SatelliteRoleSecret credential)
    {
        var builder = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString!)
        {
            Username = credential.RoleName,
            Password = credential.Password,
            Pooling = false,
            SearchPath = TestDatabase.Schema,
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_user", connection);
        Assert.Equal(credential.RoleName, (string?)await command.ExecuteScalarAsync());
    }

    private async Task AssertSatelliteGrantsAsync(string roleName)
    {
        Assert.True(await ScalarBoolAsync(
            "SELECT has_schema_privilege(@roleName, @schemaName, 'USAGE')",
            ("roleName", roleName),
            ("schemaName", TestDatabase.Schema)));

        var tableCount = await ScalarLongAsync(
            """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = @schemaName
              AND table_type = 'BASE TABLE'
            """,
            ("schemaName", TestDatabase.Schema));
        Assert.True(tableCount > 0);

        var tableGrantCount = await ScalarLongAsync(
            """
            SELECT count(*)
            FROM information_schema.tables AS tables
            WHERE tables.table_schema = @schemaName
              AND tables.table_type = 'BASE TABLE'
              AND has_table_privilege(@roleName, format('%I.%I', tables.table_schema, tables.table_name), 'SELECT')
              AND has_table_privilege(@roleName, format('%I.%I', tables.table_schema, tables.table_name), 'INSERT')
              AND has_table_privilege(@roleName, format('%I.%I', tables.table_schema, tables.table_name), 'UPDATE')
              AND has_table_privilege(@roleName, format('%I.%I', tables.table_schema, tables.table_name), 'DELETE')
            """,
            ("roleName", roleName),
            ("schemaName", TestDatabase.Schema));
        Assert.Equal(tableCount, tableGrantCount);

        var sequenceCount = await ScalarLongAsync(
            """
            SELECT count(*)
            FROM information_schema.sequences
            WHERE sequence_schema = @schemaName
            """,
            ("schemaName", TestDatabase.Schema));
        var sequenceGrantCount = await ScalarLongAsync(
            """
            SELECT count(*)
            FROM information_schema.sequences AS sequences
            WHERE sequences.sequence_schema = @schemaName
              AND has_sequence_privilege(@roleName, format('%I.%I', sequences.sequence_schema, sequences.sequence_name), 'USAGE')
              AND has_sequence_privilege(@roleName, format('%I.%I', sequences.sequence_schema, sequences.sequence_name), 'SELECT')
            """,
            ("roleName", roleName),
            ("schemaName", TestDatabase.Schema));
        Assert.Equal(sequenceCount, sequenceGrantCount);

        Assert.Equal(4, await DefaultPrivilegeCountAsync(roleName, objectType: "r"));
        Assert.Equal(2, await DefaultPrivilegeCountAsync(roleName, objectType: "S"));
    }

    private async Task<long> DefaultPrivilegeCountAsync(string roleName, string objectType) =>
        await ScalarLongAsync(
            """
            SELECT count(DISTINCT exploded.privilege_type)
            FROM pg_catalog.pg_default_acl AS acl
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = acl.defaclnamespace
            CROSS JOIN LATERAL aclexplode(acl.defaclacl) AS exploded
            JOIN pg_catalog.pg_roles AS grantee ON grantee.oid = exploded.grantee
            WHERE namespace.nspname = @schemaName
              AND grantee.rolname = @roleName
              AND acl.defaclobjtype = @objectType
            """,
            ("roleName", roleName),
            ("schemaName", TestDatabase.Schema),
            ("objectType", objectType));

    private async Task CleanupSatelliteRoleAsync(string roleName)
    {
        if (!await RoleExistsAsync(roleName))
        {
            return;
        }

        await using (var terminate = _dataSource!.CreateCommand(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_catalog.pg_stat_activity
            WHERE usename = @roleName
              AND pid <> pg_backend_pid()
            """))
        {
            terminate.Parameters.AddWithValue("roleName", roleName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = _dataSource!.CreateCommand($"""
            DROP OWNED BY {QuoteIdentifier(roleName)};
            DROP ROLE IF EXISTS {QuoteIdentifier(roleName)};
            """);
        await drop.ExecuteNonQueryAsync();
    }

    private async Task<bool> RoleExistsAsync(string roleName) =>
        await ScalarBoolAsync(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @roleName)",
            ("roleName", roleName));

    private async Task<bool> ScalarBoolAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = _dataSource!.CreateCommand(sql);
        AddParameters(command, parameters);
        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL did not return a boolean result."));
    }

    private async Task<long> ScalarLongAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = _dataSource!.CreateCommand(sql);
        AddParameters(command, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameters(NpgsqlCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    private sealed class TestDbContextFactory(Npgsql.NpgsqlDataSource dataSource) : IDbContextFactory<ViegardDbContext>
    {
        public ViegardDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<ViegardDbContext>();
            ViegardDbContextConfiguration.Configure(builder, dataSource, TestDatabase.Schema);
            return new ViegardDbContext(builder.Options);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class RecordingDiagnostics : IIngestionFilterDiagnostics
    {
        public List<IngestionFilter> InvalidFilters { get; } = [];

        public List<Exception> RefreshFailures { get; } = [];

        public void InvalidFilterSkipped(IngestionFilter filter, string reason) => InvalidFilters.Add(filter);

        public void RefreshFailed(Exception exception) => RefreshFailures.Add(exception);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
