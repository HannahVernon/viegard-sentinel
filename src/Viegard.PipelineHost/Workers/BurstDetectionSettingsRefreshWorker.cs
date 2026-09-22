using Viegard.Application.Burst;

namespace Viegard.PipelineHost.Workers;

public sealed class BurstDetectionSettingsRefreshWorker(
    BurstDetectionSettingsSource source,
    ILogger<BurstDetectionSettingsRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Burst-detection settings refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Burst-detection settings refresh worker stopping.");
        }
    }
}
