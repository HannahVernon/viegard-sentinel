using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Correlation;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;

namespace Viegard.PipelineHost.Workers;

public sealed class CorrelationWorker(
    IWorkQueue<Guid> eventQueue,
    IWorkQueue<IncidentWorkItem> incidentQueue,
    IEventStore eventStore,
    ICorrelator correlator,
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