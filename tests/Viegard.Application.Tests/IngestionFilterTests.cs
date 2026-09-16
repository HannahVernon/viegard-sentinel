using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Viegard.Application.Audit;
using Viegard.Application.Configuration;
using Viegard.Application.Queues;
using Viegard.Application.Sources;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Events;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Workers;
using Viegard.Sources.MDaemonLogs;

namespace Viegard.Application.Tests;

public sealed class IngestionFilterTests
{
    [Fact]
    public async Task Suppressed_kind_is_not_emitted_but_raw_observation_is_stored()
    {
        var filterStore = new InMemoryIngestionFilterStore();
        await filterStore.SaveMatrixAsync(
            MDaemonIngestionFilterPolicy.SourceType,
            Matrix((MDaemonEventKind.SessionLine, true)),
            MDaemonIngestionFilterPolicy.LockedEventKindNames,
            "test",
            DateTimeOffset.UtcNow);
        var filterSource = new IngestionFilterSource(filterStore);
        await filterSource.RefreshAsync();
        var rawStore = new InMemoryRawObservationStore();
        var eventStore = new InMemoryEventStore();
        var queue = new ChannelWorkQueue<Guid>("events");
        var worker = Worker(rawStore, eventStore, queue, filterSource);
        var item = Observed(
            MDaemonLogKind.SmtpIn,
            "Tue 2026-08-18 00:00:48.993: 05: Session 09012313; child 0001");

        await worker.IngestAsync(Source, Normalizer, item, CancellationToken.None);

        Assert.Equal(item.RawPayload, await rawStore.GetPayloadAsync(item.Observation.PayloadReference));
        Assert.Empty((await eventStore.ListPageAsync(beforeId: null, pageSize: 10)).Items);
        Assert.Equal(0, (await queue.GetStatsAsync()).TotalEnqueued);
    }

    [Fact]
    public async Task Unsuppressed_kind_is_emitted_and_queued()
    {
        var filterStore = new InMemoryIngestionFilterStore();
        await filterStore.SaveMatrixAsync(
            MDaemonIngestionFilterPolicy.SourceType,
            Matrix((MDaemonEventKind.SessionLine, true)),
            MDaemonIngestionFilterPolicy.LockedEventKindNames,
            "test",
            DateTimeOffset.UtcNow);
        var filterSource = new IngestionFilterSource(filterStore);
        await filterSource.RefreshAsync();
        var rawStore = new InMemoryRawObservationStore();
        var eventStore = new InMemoryEventStore();
        var queue = new ChannelWorkQueue<Guid>("events");
        var worker = Worker(rawStore, eventStore, queue, filterSource);
        var item = Observed(
            MDaemonLogKind.SmtpIn,
            "Tue 2026-08-18 00:00:48.993: 05: Accepting SMTP connection from 203.0.113.10:45584 to 192.168.0.10:25");

        await worker.IngestAsync(Source, Normalizer, item, CancellationToken.None);

        Assert.Equal(item.RawPayload, await rawStore.GetPayloadAsync(item.Observation.PayloadReference));
        var emitted = Assert.Single((await eventStore.ListPageAsync(beforeId: null, pageSize: 10)).Items);
        Assert.Equal(MDaemonEventKind.ConnectionAccepted, Assert.IsType<MDaemonLogEvent>(emitted.Payload).EventKind);
        Assert.Equal(1, (await queue.GetStatsAsync()).TotalEnqueued);
    }

    [Fact]
    public async Task Locked_kinds_cannot_be_saved_as_suppressed()
    {
        var store = new InMemoryIngestionFilterStore();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveMatrixAsync(
                MDaemonIngestionFilterPolicy.SourceType,
                Matrix((MDaemonEventKind.AuthenticationFailed, true)),
                MDaemonIngestionFilterPolicy.LockedEventKindNames,
                "test",
                DateTimeOffset.UtcNow).AsTask());

        Assert.Contains("AuthenticationFailed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Seeding_suppresses_defaults_once_without_locked_kinds_or_overwriting_edits()
    {
        var store = new InMemoryIngestionFilterStore();
        await store.SeedDefaultsIfMissingAsync(DateTimeOffset.UtcNow);

        var seeded = await store.ListForSourceAsync(MDaemonIngestionFilterPolicy.SourceType);
        Assert.Equal(
            [MDaemonEventKind.Other.ToString(), MDaemonEventKind.SessionLine.ToString()],
            seeded.Where(filter => filter.Suppressed).Select(filter => filter.EventKind).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(seeded, filter => MDaemonIngestionFilterPolicy.IsLocked(filter.EventKind) && filter.Suppressed);

        await store.SaveMatrixAsync(
            MDaemonIngestionFilterPolicy.SourceType,
            Matrix((MDaemonEventKind.SessionLine, false), (MDaemonEventKind.Other, true)),
            MDaemonIngestionFilterPolicy.LockedEventKindNames,
            "operator",
            DateTimeOffset.UtcNow);
        await store.SeedDefaultsIfMissingAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        var afterSecondSeed = await store.ListForSourceAsync(MDaemonIngestionFilterPolicy.SourceType);
        Assert.False(afterSecondSeed.Single(filter => filter.EventKind == MDaemonEventKind.SessionLine.ToString()).Suppressed);
        Assert.True(afterSecondSeed.Single(filter => filter.EventKind == MDaemonEventKind.Other.ToString()).Suppressed);
    }

    [Fact]
    public async Task Refresh_failure_fails_open_and_warns()
    {
        var diagnostics = new RecordingDiagnostics();
        var source = new IngestionFilterSource(new ThrowingIngestionFilterStore(), diagnostics);

        await source.RefreshAsync();

        Assert.Single(diagnostics.RefreshFailures);
        Assert.True(source.ShouldEmit(MDaemonEvent(MDaemonEventKind.Other)));
    }

    [Fact]
    public async Task Invalid_rows_and_locked_suppressions_are_skipped_with_warnings()
    {
        var diagnostics = new RecordingDiagnostics();
        var source = new IngestionFilterSource(new StaticIngestionFilterStore(
        [
            Filter("NotARealKind", suppressed: true),
            Filter(MDaemonEventKind.AuthenticationFailed.ToString(), suppressed: true),
            Filter(MDaemonEventKind.Other.ToString(), suppressed: true),
        ]), diagnostics);

        await source.RefreshAsync();

        Assert.Equal(2, diagnostics.InvalidFilters.Count);
        Assert.True(source.ShouldEmit(MDaemonEvent(MDaemonEventKind.AuthenticationFailed)));
        Assert.False(source.ShouldEmit(MDaemonEvent(MDaemonEventKind.Other)));
    }

    [Fact]
    public async Task Replayed_observation_is_skipped_without_error_or_duplicate_event()
    {
        var filterSource = new IngestionFilterSource(new InMemoryIngestionFilterStore());
        await filterSource.RefreshAsync();
        var rawStore = new InMemoryRawObservationStore();
        var eventStore = new InMemoryEventStore();
        var queue = new ChannelWorkQueue<Guid>("events");
        var worker = Worker(rawStore, eventStore, queue, filterSource);
        var item = Observed(
            MDaemonLogKind.SmtpIn,
            "Tue 2026-08-18 00:00:48.993: 05: Accepting SMTP connection from 203.0.113.10:45584 to 192.168.0.10:25");

        await worker.IngestAsync(Source, Normalizer, item, CancellationToken.None);
        await worker.IngestAsync(Source, Normalizer, item, CancellationToken.None);

        Assert.Single((await eventStore.ListPageAsync(beforeId: null, pageSize: 10)).Items);
        Assert.Equal(1, (await queue.GetStatsAsync()).TotalEnqueued);
    }

    private static readonly IDataSource Source = new StubDataSource();
    private static readonly IEventNormalizer Normalizer = new MDaemonEventNormalizer(new MDaemonSourceOptions());

    private static IngestionWorker Worker(
        IRawObservationStore rawStore,
        IEventStore eventStore,
        ChannelWorkQueue<Guid> queue,
        IngestionFilterSource filterSource) =>
        new(
            [Source],
            [Normalizer],
            rawStore,
            eventStore,
            filterSource,
            queue,
            new RecordingAuditLedger(),
            NullLogger<IngestionWorker>.Instance);

    private static IReadOnlyDictionary<string, bool> Matrix(params (MDaemonEventKind Kind, bool Suppressed)[] overrides)
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

    private static ObservedItem Observed(MDaemonLogKind logKind, string line)
    {
        var payloadReference = $"mdaemon/test/{ViegardId.New():N}";
        return new ObservedItem
        {
            Observation = new RawObservation
            {
                Id = ViegardId.New(),
                SourceId = "mdaemon:logs",
                SourceType = MDaemonIngestionFilterPolicy.SourceType,
                ObservedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
                PayloadReference = payloadReference,
            },
            RawPayload = JsonSerializer.Serialize(new MDaemonLineDto
            {
                SchemaVersion = 1,
                LogKind = logKind,
                FileName = "MDaemon-2026-08-18-SMTP-(in).log",
                LineText = line,
                CapturedAt = new DateTimeOffset(2026, 8, 18, 12, 30, 0, TimeSpan.Zero),
            }, MDaemonJson.SerializerOptions),
        };
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

    private static IngestionFilter Filter(string eventKind, bool suppressed) => new()
    {
        SourceType = MDaemonIngestionFilterPolicy.SourceType,
        EventKind = eventKind,
        Suppressed = suppressed,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };

    private sealed class StubDataSource : IDataSource
    {
        public string SourceId => "mdaemon:logs";

        public string SourceType => MDaemonIngestionFilterPolicy.SourceType;

        public async IAsyncEnumerable<ObservedItem> ObserveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingAuditLedger : IAuditLedger
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

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

    private sealed class RecordingDiagnostics : IIngestionFilterDiagnostics
    {
        public List<IngestionFilter> InvalidFilters { get; } = [];

        public List<Exception> RefreshFailures { get; } = [];

        public void InvalidFilterSkipped(IngestionFilter filter, string reason) => InvalidFilters.Add(filter);

        public void RefreshFailed(Exception exception) => RefreshFailures.Add(exception);
    }

    private sealed class StaticIngestionFilterStore(IReadOnlyList<IngestionFilter> filters) : IIngestionFilterStore
    {
        public long CurrentChangeVersion => 0;

        public ValueTask<IReadOnlyList<IngestionFilter>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(filters);

        public ValueTask<IReadOnlyList<IngestionFilter>> ListForSourceAsync(
            string sourceType,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IngestionFilter>>(filters
                .Where(filter => string.Equals(filter.SourceType, sourceType, StringComparison.Ordinal))
                .ToList());

        public ValueTask<IngestionFilterSaveResult> SaveMatrixAsync(
            string sourceType,
            IReadOnlyDictionary<string, bool> suppressions,
            IReadOnlySet<string> lockedEventKinds,
            string updatedBy,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SeedDefaultsIfMissingAsync(DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<long> WaitForChangeAsync(
            long lastSeenVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentChangeVersion);
    }

    private sealed class ThrowingIngestionFilterStore : IIngestionFilterStore
    {
        public long CurrentChangeVersion => 0;

        public ValueTask<IReadOnlyList<IngestionFilter>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("configured failure");

        public ValueTask<IReadOnlyList<IngestionFilter>> ListForSourceAsync(
            string sourceType,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("configured failure");

        public ValueTask<IngestionFilterSaveResult> SaveMatrixAsync(
            string sourceType,
            IReadOnlyDictionary<string, bool> suppressions,
            IReadOnlySet<string> lockedEventKinds,
            string updatedBy,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("configured failure");

        public ValueTask SeedDefaultsIfMissingAsync(DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("configured failure");

        public ValueTask<long> WaitForChangeAsync(
            long lastSeenVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentChangeVersion);
    }
}
