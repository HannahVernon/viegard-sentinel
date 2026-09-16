using Microsoft.Extensions.Options;
using Viegard.Application.Policy;

namespace Viegard.PipelineHost.Workers;

public sealed class PolicyThresholdSeedWorker(
    IPolicyThresholdSettingsStore settingsStore,
    IOptions<PolicyOptions> options,
    TimeProvider timeProvider,
    ILogger<PolicyThresholdSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(PolicyThresholdSettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "Policy threshold settings seeded from environment configuration. ReviewConfidence={ReviewConfidence}; ActionConfidence={ActionConfidence}; ActionMinSeverity={ActionMinSeverity}.",
                    result.Settings.ReviewConfidence,
                    result.Settings.ActionConfidence,
                    result.Settings.ActionMinSeverity);
            }
            else
            {
                logger.LogInformation("Policy threshold settings already exist; seed skipped.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Policy threshold setting seeding failed; policy workers will use their last loaded threshold set or environment fallbacks.");
        }
    }
}
