using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class LocalModelAdvisorRefreshWorker(
    LocalModelAdvisorSource source,
    LocalModelAdvisorCategoryBandSource categoryBandSource,
    LocalModelAdvisorPromptTemplateSource promptTemplateSource,
    ILogger<LocalModelAdvisorRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Local-model advisor settings refresh worker started.");
        try
        {
            await Task.WhenAll(
                source.RunRefreshLoopAsync(stoppingToken),
                categoryBandSource.RunRefreshLoopAsync(stoppingToken),
                promptTemplateSource.RunRefreshLoopAsync(stoppingToken)).ConfigureAwait(false);
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
