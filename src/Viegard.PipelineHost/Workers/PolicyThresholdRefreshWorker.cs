using Viegard.Application.Policy;

namespace Viegard.PipelineHost.Workers;

public sealed class PolicyThresholdRefreshWorker(
    PolicyThresholdSource source,
    ILogger<PolicyThresholdRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Policy threshold refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Policy threshold refresh worker stopping.");
        }
    }
}
