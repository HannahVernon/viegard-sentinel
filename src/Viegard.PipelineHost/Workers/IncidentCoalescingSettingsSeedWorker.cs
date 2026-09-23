using Microsoft.Extensions.Options;
using Viegard.Application.Coalescing;

namespace Viegard.PipelineHost.Workers;

public sealed class IncidentCoalescingSettingsSeedWorker(
    IIncidentCoalescingSettingsStore settingsStore,
    IOptions<IncidentCoalescingOptions> options,
    TimeProvider timeProvider,
    ILogger<IncidentCoalescingSettingsSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(IncidentCoalescingSettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "Incident-coalescing settings seeded from environment configuration. Enabled={Enabled}; SettleWindowSeconds={SettleWindowSeconds}; MaxCoalesceWindowSeconds={MaxCoalesceWindowSeconds}.",
                    result.Settings.Enabled,
                    result.Settings.SettleWindowSeconds,
                    result.Settings.MaxCoalesceWindowSeconds);
            }
            else
            {
                logger.LogInformation("Incident-coalescing settings already exist; seed skipped.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Incident-coalescing setting seeding failed; the correlator will use its last loaded setting set or environment fallbacks.");
        }
    }
}
