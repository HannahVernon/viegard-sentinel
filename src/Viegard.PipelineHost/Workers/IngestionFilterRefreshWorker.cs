using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class IngestionFilterRefreshWorker(
    IngestionFilterSource source,
    ILogger<IngestionFilterRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ingestion filter refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Ingestion filter refresh worker stopping.");
        }
    }
}
