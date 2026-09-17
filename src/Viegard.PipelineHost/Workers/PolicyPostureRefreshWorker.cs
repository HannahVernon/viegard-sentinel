using Viegard.Application.Policy;

namespace Viegard.PipelineHost.Workers;

public sealed class PolicyPostureRefreshWorker(
    PolicyPostureSource source,
    ILogger<PolicyPostureRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Policy posture refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Policy posture refresh worker stopping.");
        }
    }
}
