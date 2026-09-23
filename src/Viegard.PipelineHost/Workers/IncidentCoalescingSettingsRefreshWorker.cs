using Viegard.Application.Coalescing;

namespace Viegard.PipelineHost.Workers;

public sealed class IncidentCoalescingSettingsRefreshWorker(
    IncidentCoalescingSettingsSource source,
    ILogger<IncidentCoalescingSettingsRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Incident-coalescing settings refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Incident-coalescing settings refresh worker stopping.");
        }
    }
}
