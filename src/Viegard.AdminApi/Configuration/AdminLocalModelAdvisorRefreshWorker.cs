using Viegard.Application.Configuration;

namespace Viegard.AdminApi.Configuration;

public sealed class AdminLocalModelAdvisorRefreshWorker(
    LocalModelAdvisorSource source,
    LocalModelAdvisorCategoryBandSource categoryBandSource,
    LocalModelAdvisorPromptTemplateSource promptTemplateSource,
    ILogger<AdminLocalModelAdvisorRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Admin local-model advisor refresh worker started.");
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
            logger.LogInformation("Admin local-model advisor refresh worker stopping.");
        }
    }
}
