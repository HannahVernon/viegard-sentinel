using Microsoft.Extensions.Options;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Logs the host topology at startup and emits a periodic heartbeat.  The
/// heartbeat will publish per-queue telemetry to shared persistence once the
/// database is chosen (D-0004, D-0012); for now it keeps the host observably
/// alive in logs.
/// </summary>
public sealed class PipelineStartupService(
    IOptions<ViegardHostOptions> options,
    ILogger<PipelineStartupService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = options.Value;
        logger.LogInformation(
            "Viegard pipeline host '{InstanceId}' starting with roles: {Roles}.",
            host.EffectiveInstanceId,
            string.Join(", ", host.Roles));

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                logger.LogDebug("Heartbeat from pipeline host '{InstanceId}'.", host.EffectiveInstanceId);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        logger.LogInformation("Viegard pipeline host '{InstanceId}' stopping.", host.EffectiveInstanceId);
    }
}
