using Microsoft.Extensions.Options;
using Viegard.Application.Queues;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Publishes a telemetry snapshot for every registered queue on a fixed
/// interval (D-0012).  The admin service derives traffic-light status from
/// these snapshots; if this publisher dies, snapshots go stale, which the
/// evaluator reports as red.
/// </summary>
public sealed class QueueTelemetryPublisher(
    IEnumerable<IQueueStatsSource> queues,
    IQueueTelemetryStore telemetryStore,
    IOptions<ViegardHostOptions> hostOptions,
    IConfiguration configuration,
    ILogger<QueueTelemetryPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("Viegard:Telemetry:PublishIntervalSeconds", 10);
        var interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        var instanceId = hostOptions.Value.EffectiveInstanceId;

        logger.LogInformation(
            "Queue telemetry publisher started (interval {Interval}s, {QueueCount} queue(s) registered).",
            interval.TotalSeconds,
            queues.Count());

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var queue in queues)
                {
                    var stats = queue.GetStats();
                    await telemetryStore.PublishAsync(
                        new QueueTelemetrySnapshot
                        {
                            InstanceId = instanceId,
                            QueueName = stats.QueueName,
                            Depth = stats.Depth,
                            InFlight = stats.InFlight,
                            OldestPendingEnqueuedAt = stats.OldestPendingEnqueuedAt,
                            TotalEnqueued = stats.TotalEnqueued,
                            TotalCompleted = stats.TotalCompleted,
                            TotalAbandoned = stats.TotalAbandoned,
                            DeadLetterCount = stats.DeadLetterCount,
                            CapturedAt = DateTimeOffset.UtcNow,
                        },
                        stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
