using Microsoft.Extensions.Options;
using Viegard.Application.Auth;

namespace Viegard.AdminApi.Auth;

/// <summary>
/// Seeds the durable session-security settings row from environment fallbacks on
/// startup, then keeps the in-process <see cref="SessionSecuritySettingsSource"/>
/// current so the step-up gate and pending-action capture read live values.
/// Combines the seed and refresh responsibilities because these settings are an
/// Admin-API concern with no pipeline-host consumer.
/// </summary>
public sealed class AdminSessionSecurityRefreshWorker(
    SessionSecuritySettingsSource source,
    ISessionSecuritySettingsStore settingsStore,
    IOptions<SessionSecurityOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminSessionSecurityRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Admin session-security refresh worker started.");
        try
        {
            await SeedAsync(stoppingToken).ConfigureAwait(false);
            await source.RunRefreshLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("Admin session-security refresh worker stopping.");
        }
    }

    private async Task SeedAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(SessionSecuritySettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "Session-security settings seeded from environment configuration. StepUpValiditySeconds={StepUpValiditySeconds}; ResumeStashTtlSeconds={ResumeStashTtlSeconds}.",
                    result.Settings.StepUpValiditySeconds,
                    result.Settings.ResumeStashTtlSeconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session-security setting seeding failed; the step-up gate will use environment fallbacks until the row is created.");
        }
    }
}
