using Viegard.Application.Detection;

namespace Viegard.PipelineHost.Workers;

public sealed class CustomSignatureRefreshWorker(
    CustomSignatureRuleSource source,
    ILogger<CustomSignatureRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Custom signature refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            logger.LogInformation("Custom signature refresh worker stopping.");
        }
    }
}
