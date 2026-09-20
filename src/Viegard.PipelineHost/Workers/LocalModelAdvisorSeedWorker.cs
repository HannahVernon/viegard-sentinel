using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class LocalModelAdvisorSeedWorker(
    ILocalModelAdvisorSettingsStore settingsStore,
    IOptions<LocalModelAdvisorOptions> options,
    TimeProvider timeProvider,
    ILogger<LocalModelAdvisorSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(LocalModelAdvisorSettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "Local-model advisor settings seeded from environment configuration. Enabled={Enabled}; Endpoint={Endpoint}; Model={Model}.",
                    result.Settings.Enabled,
                    result.Settings.Endpoint,
                    result.Settings.Model);
            }
            else
            {
                logger.LogInformation("Local-model advisor settings already exist; seed skipped.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Local-model advisor seeding failed; classification will use the last loaded settings or disabled fallback.");
        }
    }
}
