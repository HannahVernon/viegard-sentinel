using Viegard.Application.Doh;

namespace Viegard.PipelineHost.Workers;

public sealed class DohBlocklistSettingsRefreshWorker(
    DohBlocklistSettingsSource source,
    ILogger<DohBlocklistSettingsRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DoH blocklist settings refresh worker started.");
        try
        {
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("DoH blocklist settings refresh worker stopping.");
        }
    }
}
