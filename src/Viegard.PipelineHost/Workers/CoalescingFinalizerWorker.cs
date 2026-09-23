using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Incidents;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Closes incident coalescing windows.  A provisional decision is emitted the
/// moment an incident is created; this worker polls for incidents whose quiet
/// window has elapsed and, when late same-source events were merged in after
/// that provisional decision, re-opens the incident so the classification and
/// policy stages produce one merged decision that supersedes the provisional
/// one.  Incidents that did not grow are simply finalized.
/// </summary>
public sealed class CoalescingFinalizerWorker(
    IIncidentStore incidentStore,
    IWorkQueue<IncidentWorkItem> incidentQueue,
    IAuditLedger auditLedger,
    TimeProvider timeProvider,
    ILogger<CoalescingFinalizerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 64;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Coalescing finalizer worker started.");
        using var timer = new PeriodicTimer(PollInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await ProcessReadyIncidentsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Coalescing finalizer worker failed a polling cycle.");
            }
        }

        logger.LogInformation("Coalescing finalizer worker stopping.");
    }

    private async Task ProcessReadyIncidentsAsync(CancellationToken cancellationToken)
    {
        var ready = await incidentStore
            .ListCoalescingReadyAsync(timeProvider.GetUtcNow(), BatchSize, cancellationToken)
            .ConfigureAwait(false);

        foreach (var incident in ready)
        {
            var grew = incident.DecidedEventCount > 0
                && incident.EventIds.Count > incident.DecidedEventCount;

            if (grew)
            {
                // Clearing CoalesceUntil removes the incident from both the
                // correlator's absorb set and this worker's ready set, so it is
                // re-classified exactly once for the merged final decision.
                await incidentStore
                    .UpsertAsync(
                        incident with { State = IncidentState.Open, CoalesceUntil = null },
                        cancellationToken)
                    .ConfigureAwait(false);
                await incidentQueue
                    .EnqueueAsync(new IncidentWorkItem(incident.Id), cancellationToken)
                    .ConfigureAwait(false);
                await auditLedger.AppendAsync(new AuditRecord
                {
                    Id = ViegardId.New(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Stage = PipelineStage.Correlation,
                    Summary = $"Coalescing window closed for incident {incident.Id}; re-deciding across {incident.EventIds.Count} merged events.",
                    IncidentId = incident.Id,
                    DetailJson = JsonSerializer.Serialize(new
                    {
                        Kind = "CoalescingRedecide",
                        incident.DecidedEventCount,
                        MergedEventCount = incident.EventIds.Count,
                    }),
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await incidentStore
                    .UpsertAsync(
                        incident with { State = IncidentState.Closed, CoalesceUntil = null },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
