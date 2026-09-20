using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Viegard.Application.Audit;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain.Audit;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class RetentionWorkerTests
{
    [Fact]
    public async Task Worker_seeds_missing_settings_then_runs_after_startup_delay_and_repeats_each_interval()
    {
        var start = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var purgeStore = new RecordingRetentionStore();
        var settingsStore = new RecordingRetentionSettingsStore();
        var worker = CreateWorker(
            purgeStore,
            settingsStore,
            new RecordingAuditLedger(),
            new RetentionOptions
            {
                EventsDays = 90,
                StartupDelay = TimeSpan.FromSeconds(1),
                CheckInterval = TimeSpan.FromHours(24),
            },
            time);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(50);
            Assert.Equal(1, settingsStore.SeedCalls);
            Assert.Empty(purgeStore.Calls);

            time.Advance(TimeSpan.FromSeconds(1));
            await WaitForAsync(() => purgeStore.Calls.Count >= 2);
            var first = purgeStore.Calls.Single(call => call.Target == RetentionTarget.Events);
            Assert.Equal(RetentionTarget.Events, first.Target);
            Assert.Equal(start.AddSeconds(1).AddDays(-90), first.Cutoff);
            Assert.Contains(purgeStore.Calls, call => call.Target == RetentionTarget.LocalModelAdvisorConsults);

            time.Advance(TimeSpan.FromHours(24));
            await WaitForAsync(() => purgeStore.Calls.Count >= 4);
            var second = purgeStore.Calls.Where(call => call.Target == RetentionTarget.Events).ElementAt(1);
            Assert.Equal(RetentionTarget.Events, second.Target);
            Assert.Equal(start.AddSeconds(1).AddHours(24).AddDays(-90), second.Cutoff);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cycle_reads_database_periods_instead_of_environment_periods()
    {
        var start = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var purgeStore = new RecordingRetentionStore();
        var settingsStore = new RecordingRetentionSettingsStore(SettingsWith((RetentionTarget.Events, 90)));
        var worker = CreateWorker(
            purgeStore,
            settingsStore,
            new RecordingAuditLedger(),
            new RetentionOptions
            {
                EventsDays = 1,
                BatchSize = 10,
            },
            time);

        await worker.RunCycleAsync();

        var call = Assert.Single(purgeStore.Calls);
        Assert.Equal(RetentionTarget.Events, call.Target);
        Assert.Equal(start.AddDays(-90), call.Cutoff);
        Assert.Equal(10, call.BatchSize);
    }

    [Fact]
    public async Task Cycle_purges_only_configured_targets_and_audits_deleted_rows()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero));
        var purgeStore = new RecordingRetentionStore();
        purgeStore.SetRowsDeleted(RetentionTarget.Incidents, 2);
        purgeStore.SetRowsDeleted(RetentionTarget.DeadLetteredQueueMessages, 3);
        var settingsStore = new RecordingRetentionSettingsStore(SettingsWith(
            (RetentionTarget.Events, 90),
            (RetentionTarget.Incidents, 180),
            (RetentionTarget.DeadLetteredQueueMessages, 30)));
        var audit = new RecordingAuditLedger();
        var worker = CreateWorker(
            purgeStore,
            settingsStore,
            audit,
            new RetentionOptions { BatchSize = 10 },
            time);

        var result = await worker.RunCycleAsync();

        Assert.Equal(5, result.TotalDeleted);
        Assert.Equal(
            [RetentionTarget.Events, RetentionTarget.Incidents, RetentionTarget.DeadLetteredQueueMessages],
            purgeStore.Calls.Select(c => c.Target));
        Assert.All(purgeStore.Calls, call => Assert.Equal(10, call.BatchSize));

        var record = Assert.Single(audit.Records);
        Assert.Equal(PipelineStage.System, record.Stage);
        Assert.Equal("Retention purge removed 5 rows.", record.Summary);
        using var detail = JsonDocument.Parse(record.DetailJson!);
        var targets = detail.RootElement.GetProperty("Targets").EnumerateArray().ToList();
        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, t =>
            t.GetProperty("TableName").GetString() == "incidents"
            && t.GetProperty("Days").GetInt32() == 180
            && t.GetProperty("RowsDeleted").GetInt64() == 2);

        var cycleUpdate = Assert.Single(settingsStore.LastCycleUpdates);
        Assert.Equal(5, cycleUpdate.Counts.Values.Sum());
        Assert.Equal(3, cycleUpdate.Counts[RetentionTarget.DeadLetteredQueueMessages]);
    }

    [Fact]
    public async Task Cycle_with_no_deleted_rows_updates_last_cycle_and_writes_no_audit_record()
    {
        var now = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);
        var purgeStore = new RecordingRetentionStore();
        var settingsStore = new RecordingRetentionSettingsStore(SettingsWith((RetentionTarget.Events, 90)));
        var audit = new RecordingAuditLedger();
        var worker = CreateWorker(
            purgeStore,
            settingsStore,
            audit,
            new RetentionOptions(),
            new FakeTimeProvider(now));

        var result = await worker.RunCycleAsync();

        Assert.Equal(0, result.TotalDeleted);
        Assert.Empty(audit.Records);
        var cycleUpdate = Assert.Single(settingsStore.LastCycleUpdates);
        Assert.Equal(now, cycleUpdate.LastCycleAt);
        Assert.All(RetentionSettings.Targets, target => Assert.Equal(0, cycleUpdate.Counts[target]));
    }

    [Fact]
    public async Task Cycle_with_settings_row_but_no_configured_periods_updates_last_cycle_without_purging()
    {
        var now = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);
        var purgeStore = new RecordingRetentionStore();
        var settingsStore = new RecordingRetentionSettingsStore(SettingsWith());
        var audit = new RecordingAuditLedger();
        var worker = CreateWorker(
            purgeStore,
            settingsStore,
            audit,
            new RetentionOptions(),
            new FakeTimeProvider(now));

        var result = await worker.RunCycleAsync();

        Assert.Equal(0, result.TotalDeleted);
        Assert.Empty(result.Targets);
        Assert.Empty(purgeStore.Calls);
        Assert.Empty(audit.Records);
        var cycleUpdate = Assert.Single(settingsStore.LastCycleUpdates);
        Assert.Equal(now, cycleUpdate.LastCycleAt);
    }

    [Fact]
    public async Task Cycle_without_settings_row_is_a_noop()
    {
        var purgeStore = new RecordingRetentionStore();
        var settingsStore = new RecordingRetentionSettingsStore();
        var audit = new RecordingAuditLedger();
        var worker = CreateWorker(
            purgeStore,
            settingsStore,
            audit,
            new RetentionOptions { EventsDays = 90 },
            new FakeTimeProvider());

        var result = await worker.RunCycleAsync();

        Assert.Equal(0, result.TotalDeleted);
        Assert.Empty(result.Targets);
        Assert.Empty(purgeStore.Calls);
        Assert.Empty(audit.Records);
        Assert.Single(settingsStore.LastCycleUpdates);
    }

    [Fact]
    public void Maintenance_role_enables_retention_worker()
    {
        var hostOptions = new ViegardHostOptions();
        hostOptions.Roles.Add(RoleNames.Maintenance);

        Assert.True(new ViegardHostOptionsValidator().Validate(Options.DefaultName, hostOptions).Succeeded);

        var services = new ServiceCollection();
        services.AddMaintenanceWorkers([RoleNames.Maintenance]);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(RetentionWorker));
    }

    [Fact]
    public void Missing_maintenance_role_does_not_enable_retention_worker()
    {
        var services = new ServiceCollection();
        services.AddMaintenanceWorkers([RoleNames.Correlation]);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(RetentionWorker));
    }

    private static RetentionWorker CreateWorker(
        IRetentionStore store,
        IRetentionSettingsStore settingsStore,
        IAuditLedger auditLedger,
        RetentionOptions options,
        TimeProvider timeProvider) =>
        new(
            store,
            settingsStore,
            auditLedger,
            Options.Create(options),
            timeProvider,
            NullLogger<RetentionWorker>.Instance);

    private static RetentionSettings SettingsWith(params (RetentionTarget Target, int? Days)[] values)
    {
        var settings = new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
            LocalModelAdvisorConsultsDays = null,
        };
        foreach (var (target, days) in values)
        {
            settings = settings.WithDays(target, days);
        }

        return settings;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var stopAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < stopAt)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class RecordingRetentionSettingsStore(RetentionSettings? initialSettings = null) : IRetentionSettingsStore
    {
        private readonly Lock _sync = new();
        private RetentionSettings? _settings = initialSettings;

        public int SeedCalls { get; private set; }

        public List<LastCycleUpdate> LastCycleUpdates { get; } = [];

        public ValueTask<RetentionSettings?> GetAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return ValueTask.FromResult(_settings);
            }
        }

        public ValueTask<RetentionSettingsSaveResult> UpsertAsync(
            RetentionSettings settings,
            int expectedVersion,
            string updatedBy,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_settings?.Version != expectedVersion && !(_settings is null && expectedVersion == 0))
                {
                    return ValueTask.FromResult(RetentionSettingsSaveResult.Conflict(_settings));
                }

                _settings = settings with
                {
                    Version = expectedVersion + 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = updatedBy,
                };
                return ValueTask.FromResult(RetentionSettingsSaveResult.Saved(_settings));
            }
        }

        public ValueTask<RetentionSettings?> SeedIfMissingAsync(
            RetentionOptions options,
            DateTimeOffset seededAt,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                SeedCalls++;
                _settings ??= RetentionSettings.FromOptions(options, seededAt);
                return ValueTask.FromResult<RetentionSettings?>(_settings);
            }
        }

        public ValueTask UpdateLastCycleAsync(
            DateTimeOffset lastCycleAt,
            IReadOnlyDictionary<RetentionTarget, long> counts,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var snapshot = counts.ToDictionary(pair => pair.Key, pair => pair.Value);
                LastCycleUpdates.Add(new LastCycleUpdate(lastCycleAt, snapshot));
                if (_settings is not null)
                {
                    _settings = _settings with
                    {
                        LastCycleAt = lastCycleAt.ToUniversalTime(),
                        LastCycleCountsJson = RetentionSettings.SerializeCounts(snapshot),
                    };
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRetentionStore : IRetentionStore
    {
        private readonly Lock _sync = new();
        private readonly List<RetentionCall> _calls = [];
        private readonly Dictionary<RetentionTarget, Queue<long>> _rowsDeleted = [];

        public IReadOnlyList<RetentionCall> Calls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.ToList();
                }
            }
        }

        public void SetRowsDeleted(RetentionTarget target, params long[] rowsDeleted)
        {
            lock (_sync)
            {
                _rowsDeleted[target] = new Queue<long>(rowsDeleted);
            }
        }

        public ValueTask<long> PurgeAsync(
            RetentionTarget target,
            DateTimeOffset cutoff,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _calls.Add(new RetentionCall(target, cutoff, batchSize));
                return ValueTask.FromResult(
                    _rowsDeleted.TryGetValue(target, out var results) && results.Count > 0
                        ? results.Dequeue()
                        : 0L);
            }
        }
    }

    private sealed class RecordingAuditLedger : IAuditLedger
    {
        private readonly List<AuditRecord> _records = [];

        public IReadOnlyList<AuditRecord> Records => _records;

        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            _records.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<KeysetPage<AuditRecord>> ListPageAsync(
            Guid? beforeId,
            int pageSize,
            AuditListFilter? filter = null,
            ListSort<AuditSortColumn>? sort = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KeysetPage<AuditRecord>([], null, 0, 0));

        public ValueTask<Guid?> GetPageCursorAsync(
            int pageNumber,
            int pageSize,
            AuditListFilter? filter = null,
            ListSort<AuditSortColumn>? sort = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Guid?>(null);
    }

    private sealed record RetentionCall(RetentionTarget Target, DateTimeOffset Cutoff, int BatchSize);

    private sealed record LastCycleUpdate(DateTimeOffset LastCycleAt, IReadOnlyDictionary<RetentionTarget, long> Counts);
}
