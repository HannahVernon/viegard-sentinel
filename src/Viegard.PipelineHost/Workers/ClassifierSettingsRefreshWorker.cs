using Viegard.Application.Classifiers;

namespace Viegard.PipelineHost.Workers;

public sealed class ClassifierSettingsRefreshWorker(
    ClassifierSettingsSource source,
    ILogger<ClassifierSettingsRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Classifier settings refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Classifier settings refresh worker stopping.");
        }
    }
}
