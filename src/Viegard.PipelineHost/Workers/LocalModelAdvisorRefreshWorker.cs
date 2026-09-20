using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class LocalModelAdvisorRefreshWorker(
    LocalModelAdvisorSource source,
    ILogger<LocalModelAdvisorRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Local-model advisor settings refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Local-model advisor settings refresh worker stopping.");
        }
    }
}
