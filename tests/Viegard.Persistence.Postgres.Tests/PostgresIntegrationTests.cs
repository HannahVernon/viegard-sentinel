using Microsoft.EntityFrameworkCore;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Configuration;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Application.Retention;
using Viegard.Domain.Actions;
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
            "TRUNCATE queue_messages, queue_counters, retention_settings");
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
