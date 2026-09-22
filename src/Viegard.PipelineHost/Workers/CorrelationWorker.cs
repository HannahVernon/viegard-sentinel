using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Burst;
using Viegard.Application.Correlation;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.PipelineHost.Workers;

public sealed class CorrelationWorker(
    IWorkQueue<Guid> eventQueue,
    IWorkQueue<IncidentWorkItem> incidentQueue,
    IEventStore eventStore,
    ICorrelator correlator,
    IIncidentStore incidentStore,
    BurstDetector burstDetector,
    IAuditLedger auditLedger,
    ILogger<CorrelationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Correlation worker started for queue '{QueueName}'.", eventQueue.QueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            IWorkLease<Guid>? lease = null;
            try
            {
                lease = await eventQueue.LeaseAsync(stoppingToken).ConfigureAwait(false);
                var eventId = lease.Message;
                var normalizedEvent = await eventStore.GetAsync(eventId, stoppingToken).ConfigureAwait(false);
                if (normalizedEvent is null)
                {
                    logger.LogWarning("Correlation worker could not find event {EventId}; completing lease.", eventId);
                    await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var incidents = await correlator.CorrelateAsync(normalizedEvent, stoppingToken).ConfigureAwait(false);
                foreach (var incident in incidents)
                {
                    await auditLedger.AppendAsync(new AuditRecord
                    {
                        Id = ViegardId.New(),
                        Timestamp = DateTimeOffset.UtcNow,
                        Stage = PipelineStage.Correlation,
                        Summary = $"Correlation wrote incident {incident.Id} for event {eventId}.",
                        SourceId = normalizedEvent.SourceId,
                        EventId = eventId,
                        IncidentId = incident.Id,
                        DetailJson = CorrelationDetailJson(incident.Evidence, eventId),
                    }, stoppingToken).ConfigureAwait(false);
                    await incidentQueue
                        .EnqueueAsync(new IncidentWorkItem(incident.Id), stoppingToken)
                        .ConfigureAwait(false);
                }

                await ObserveBurstsAsync(normalizedEvent, eventId, stoppingToken).ConfigureAwait(false);

                await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (lease is not null)
                {
                    await AbandonLeaseAsync(lease, chargeAttempt: false).ConfigureAwait(false);
                }

                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Correlation worker failed while processing an event lease.");
                if (lease is not null)
                {
                    await AbandonLeaseAsync(lease, chargeAttempt: true).ConfigureAwait(false);
                }
            }
        }

        logger.LogInformation("Correlation worker stopping.");
    }

    private async Task AbandonLeaseAsync(IWorkLease<Guid> lease, bool chargeAttempt)
    {
        try
        {
            await lease.AbandonAsync(chargeAttempt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception abandonEx)
        {
            logger.LogError(
                abandonEx,
                chargeAttempt
                    ? "Correlation worker failed to abandon an event lease."
                    : "Correlation worker failed to release an event lease during shutdown.");
        }
    }

    private async Task ObserveBurstsAsync(NormalizedEvent normalizedEvent, Guid eventId, CancellationToken cancellationToken)
    {
        var firings = await burstDetector.ObserveAsync(normalizedEvent, cancellationToken).ConfigureAwait(false);
        foreach (var firing in firings)
        {
            var incident = new Incident
            {
                Id = ViegardId.New(),
                CorrelationKey = $"burst:{firing.SignalId}:{firing.SourceKey}",
                WindowStart = firing.WindowStart,
                WindowEnd = firing.WindowEnd,
                EventIds = firing.EventIds,
                Evidence =
                [
                    new EvidenceItem
                    {
                        Description =
                            $"Rate-based burst: {firing.Count} '{firing.SignalId}' occurrences from {firing.SourceKey} "
                            + $"within {firing.Window.TotalSeconds:0}s (threshold {firing.Threshold}).",
                        Score = BurstEvidenceScore(firing),
                        EventId = eventId,
                    },
                ],
                State = IncidentState.Open,
            };

            await incidentStore.UpsertAsync(incident, cancellationToken).ConfigureAwait(false);
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Correlation,
                Summary = $"Burst detector proposed incident {incident.Id} ({firing.SignalId}, {firing.Count} events) for review.",
                SourceId = normalizedEvent.SourceId,
                EventId = eventId,
                IncidentId = incident.Id,
                DetailJson = BurstDetailJson(firing),
            }, cancellationToken).ConfigureAwait(false);
            await incidentQueue
                .EnqueueAsync(new IncidentWorkItem(incident.Id), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static double BurstEvidenceScore(BurstFiring firing) =>
        Math.Min(3.0 + (0.2 * Math.Max(0, firing.Count - firing.Threshold)), 5.0);

    private static string BurstDetailJson(BurstFiring firing) =>
        JsonSerializer.Serialize(new
        {
            firing.SignalId,
            firing.SourceKey,
            firing.Count,
            firing.Threshold,
            WindowSeconds = firing.Window.TotalSeconds,
            firing.ActionEligible,
            EventCount = firing.EventIds.Count,
        });

    private static string CorrelationDetailJson(IReadOnlyList<Viegard.Domain.Incidents.EvidenceItem> evidence, Guid eventId)
    {
        var currentEventEvidence = evidence
            .Where(e => e.EventId == eventId || e.EventId is null)
            .ToList();

        var sampledEvidence = currentEventEvidence
            .Take(10)
            .Select(e => new
            {
                e.Description,
                e.Score,
                e.EventId,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            EvidenceItemsForEvent = currentEventEvidence.Count,
            Evidence = sampledEvidence,
        });
    }
}