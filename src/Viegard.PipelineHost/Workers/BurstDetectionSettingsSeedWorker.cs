using Microsoft.Extensions.Options;
using Viegard.Application.Burst;

namespace Viegard.PipelineHost.Workers;

public sealed class BurstDetectionSettingsSeedWorker(
    IBurstDetectionSettingsStore settingsStore,
    IOptions<BurstDetectionOptions> options,
    TimeProvider timeProvider,
    ILogger<BurstDetectionSettingsSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(BurstDetectionSettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "Burst-detection settings seeded from environment configuration. GlobalEnabled={GlobalEnabled}; AuthFailureEnabled={AuthFailureEnabled}; AuthFailureThreshold={AuthFailureThreshold}; AuthFailureWindowSeconds={AuthFailureWindowSeconds}; AuthFailureCooldownSeconds={AuthFailureCooldownSeconds}; AuthFailureActionEligible={AuthFailureActionEligible}.",
                    result.Settings.GlobalEnabled,
                    result.Settings.AuthFailureEnabled,
                    result.Settings.AuthFailureThreshold,
                    result.Settings.AuthFailureWindowSeconds,
                    result.Settings.AuthFailureCooldownSeconds,
                    result.Settings.AuthFailureActionEligible);
            }
            else
            {
                logger.LogInformation("Burst-detection settings already exist; seed skipped.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Burst-detection setting seeding failed; the detector will use its last loaded setting set or environment fallbacks.");
        }
    }
}
