using Microsoft.Extensions.Options;
using Viegard.Application.Policy;

namespace Viegard.PipelineHost.Workers;

public sealed class PolicyPostureSeedWorker(
    IPolicyPostureSettingsStore settingsStore,
    IOptions<PolicyOptions> options,
    TimeProvider timeProvider,
    ILogger<PolicyPostureSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(PolicyPostureSettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "Policy posture settings seeded from environment configuration. DryRun={DryRun}; ManualApprovalMode={ManualApprovalMode}; EmergencyStop={EmergencyStop}.",
                    result.Settings.DryRun,
                    result.Settings.ManualApprovalMode,
                    result.Settings.EmergencyStop);
            }
            else
            {
                logger.LogInformation("Policy posture settings already exist; seed skipped.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Policy posture seeding failed; posture consumers will use their last loaded posture or environment fallbacks.");
        }
    }
}
