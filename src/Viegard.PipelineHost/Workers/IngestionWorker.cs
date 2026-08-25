using Viegard.Application.Audit;
using Viegard.Application.Queues;
using Viegard.Application.Sources;
using Viegard.Application.Stores;
using Viegard.Domain.Audit;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Front of the pipeline (Eyes): consumes every registered data source,
/// persists raw observations, normalizes them, stores normalized events, and
/// enqueues event IDs for correlation.  Each stage transition is audited
/// separately (retrieval vs. normalization), and normalization failures are
/// recorded without stopping ingestion.
/// </summary>
public sealed class IngestionWorker(
    IEnumerable<IDataSource> sources,
    IEnumerable<IEventNormalizer> normalizers,
    IRawObservationStore rawObservationStore,
    IEventStore eventStore,
    IWorkQueue<Guid> eventQueue,
    IAuditLedger auditLedger,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sourceList = sources.ToList();
        if (sourceList.Count == 0)
        {
            logger.LogInformation("Ingestion worker: no data sources configured; idle.");
            return;
        }

        logger.LogInformation(
            "Ingestion worker starting for {Count} source(s): {Sources}.",
            sourceList.Count,
            string.Join(", ", sourceList.Select(s => s.SourceId)));

        var pumps = sourceList.Select(source => PumpSourceAsync(source, stoppingToken)).ToList();
        await Task.WhenAll(pumps).ConfigureAwait(false);
    }

    private async Task PumpSourceAsync(IDataSource source, CancellationToken cancellationToken)
    {
        var normalizer = normalizers.FirstOrDefault(n => n.SourceType == source.SourceType);
        if (normalizer is null)
        {
            logger.LogError(
                "Ingestion worker: no normalizer registered for source type '{SourceType}' (source {SourceId}); source skipped.",
                source.SourceType, source.SourceId);
            return;
        }

        try
        {
            await foreach (var item in source.ObserveAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await IngestAsync(source, normalizer, item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One failed item must never stop ingestion (or the
                    // host): log, audit, and keep consuming the source.
                    logger.LogError(
                        ex,
                        "Ingestion failed for observation {ObservationId} from {SourceId}; continuing.",
                        item.Observation.Id, source.SourceId);
                    await TryAuditIngestFailureAsync(source, item, ex, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task TryAuditIngestFailureAsync(
        IDataSource source,
        ObservedItem item,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Ingestion,
                Summary = $"Ingestion failed: {exception.GetType().Name}: {exception.Message}",
                SourceId = source.SourceId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception auditEx) when (auditEx is not OperationCanceledException)
        {
            // Auditing the failure failed too (e.g., database outage).  The
            // original error is already logged; do not take down ingestion.
            logger.LogError(auditEx, "Failed to audit an ingestion failure for {SourceId}.", source.SourceId);
        }
    }

    private async Task IngestAsync(
        IDataSource source,
        IEventNormalizer normalizer,
        ObservedItem item,
        CancellationToken cancellationToken)
    {
        await rawObservationStore.AddAsync(item.Observation, item.RawPayload, cancellationToken).ConfigureAwait(false);
        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Stage = PipelineStage.Ingestion,
            Summary = $"Observation ingested from {source.SourceId}.",
            SourceId = source.SourceId,
        }, cancellationToken).ConfigureAwait(false);

        var result = normalizer.Normalize(item.Observation, item.RawPayload);
        if (!result.Succeeded || result.Event is null)
        {
            logger.LogWarning(
                "Normalization failed for observation {ObservationId} from {SourceId}: {Reason}",
                item.Observation.Id, source.SourceId, result.FailureReason);
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Normalization,
                Summary = $"Normalization failed: {result.FailureReason}",
                SourceId = source.SourceId,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await eventStore.AddAsync(result.Event, cancellationToken).ConfigureAwait(false);
        await eventQueue.EnqueueAsync(result.Event.Id, cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Event {EventId} ({PayloadType}) normalized from {SourceId} and queued for correlation.",
            result.Event.Id, result.Event.Payload.GetType().Name, source.SourceId);
        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Stage = PipelineStage.Normalization,
            Summary = $"Event normalized ({result.Event.SourceType}) and queued for correlation.",
            SourceId = source.SourceId,
            EventId = result.Event.Id,
        }, cancellationToken).ConfigureAwait(false);
    }
}
