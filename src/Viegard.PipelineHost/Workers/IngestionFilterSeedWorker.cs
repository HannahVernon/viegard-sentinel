using Viegard.Application.Stores;

namespace Viegard.PipelineHost.Workers;

public sealed class IngestionFilterSeedWorker(
    IIngestionFilterStore filters,
    TimeProvider timeProvider,
    ILogger<IngestionFilterSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await filters.SeedDefaultsIfMissingAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            logger.LogInformation("Ingestion filter defaults verified.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ingestion filter default seeding failed; sources will continue with their last loaded filter set.");
        }
    }
}
