using Microsoft.Extensions.Options;
using Viegard.Application.Doh;

namespace Viegard.PipelineHost.Workers;

public sealed class DohBlocklistSettingsSeedWorker(
    IDohBlocklistSettingsStore settingsStore,
    IOptions<DohBlocklistOptions> options,
    TimeProvider timeProvider,
    ILogger<DohBlocklistSettingsSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(DohBlocklistSettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "DoH blocklist settings seeded from environment configuration. Enabled={Enabled}; ProbeEnabled={ProbeEnabled}; ApplyToRouters={ApplyToRouters}; AddressListName={AddressListName}.",
                    result.Settings.Enabled,
                    result.Settings.ProbeEnabled,
                    result.Settings.ApplyToRouters,
                    result.Settings.AddressListName);
            }
            else
            {
                logger.LogInformation("DoH blocklist settings already exist; seed skipped.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DoH blocklist setting seeding failed; the feature will use its last loaded setting set or environment fallbacks.");
        }
    }
}
