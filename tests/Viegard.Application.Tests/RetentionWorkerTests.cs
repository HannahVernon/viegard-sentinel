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
    public async Task Worker_runs_after_startup_delay_and_repeats_each_interval()
    {
        var start = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var store = new RecordingRetentionStore();
        var worker = CreateWorker(
            store,
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
            Assert.Empty(store.Calls);

            time.Advance(TimeSpan.FromSeconds(1));
            await WaitForAsync(() => store.Calls.Count == 1);
            var first = Assert.Single(store.Calls);
            Assert.Equal(RetentionTarget.Events, first.Target);
            Assert.Equal(start.AddSeconds(1).AddDays(-90), first.Cutoff);

            time.Advance(TimeSpan.FromHours(24));
            await WaitForAsync(() => store.Calls.Count == 2);
            var second = store.Calls[1];
            Assert.Equal(RetentionTarget.Events, second.Target);
            Assert.Equal(start.AddSeconds(1).AddHours(24).AddDays(-90), second.Cutoff);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cycle_purges_only_configured_targets_and_audits_deleted_rows()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero));
        var store = new RecordingRetentionStore();
        store.SetRowsDeleted(RetentionTarget.Incidents, 2);
        store.SetRowsDeleted(RetentionTarget.DeadLetteredQueueMessages, 3);
        var audit = new RecordingAuditLedger();
        var worker = CreateWorker(
            store,
            audit,
            new RetentionOptions
            {
                EventsDays = 90,
                IncidentsDays = 180,
                DeadLetteredQueueMessagesDays = 30,
                BatchSize = 10,
            },
            time);

        var result = await worker.RunCycleAsync();

        Assert.Equal(5, result.TotalDeleted);
        Assert.Equal(
            [RetentionTarget.Events, RetentionTarget.Incidents, RetentionTarget.DeadLetteredQueueMessages],
            store.Calls.Select(c => c.Target));
        Assert.All(store.Calls, call => Assert.Equal(10, call.BatchSize));

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
    }

    [Fact]
    public async Task Cycle_with_no_deleted_rows_writes_no_audit_record()
    {
        var store = new RecordingRetentionStore();
        var audit = new RecordingAuditLedger();
        var worker = CreateWorker(
            store,
            audit,
            new RetentionOptions { EventsDays = 90 },
            new FakeTimeProvider());

        var result = await worker.RunCycleAsync();

        Assert.Equal(0, result.TotalDeleted);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task Cycle_with_no_configured_periods_skips_store_and_audit()
    {
        var store = new RecordingRetentionStore();
        var audit = new RecordingAuditLedger();
        var worker = CreateWorker(
            store,
            audit,
            new RetentionOptions(),
            new FakeTimeProvider());

        var result = await worker.RunCycleAsync();

        Assert.Equal(0, result.TotalDeleted);
        Assert.Empty(result.Targets);
        Assert.Empty(store.Calls);
        Assert.Empty(audit.Records);
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
        IAuditLedger auditLedger,
        RetentionOptions options,
        TimeProvider timeProvider) =>
        new(
            store,
            auditLedger,
            Options.Create(options),
            timeProvider,
            NullLogger<RetentionWorker>.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var stopAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < stopAt)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
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
    }

    private sealed record RetentionCall(RetentionTarget Target, DateTimeOffset Cutoff, int BatchSize);
}
