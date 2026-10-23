using Viegard.Application.Configuration;

namespace Viegard.AdminApi.Configuration;

/// <summary>
/// One-shot startup step that upgrades legacy router credential ciphertext to the tuple-bound format (D-0070).
/// Failures are logged without secret material and never stop the host.
/// </summary>
public sealed class RouterCredentialReprotectService(
    IMikroTikRouterStore routers,
    IRouterCredentialProtector protector,
    ILogger<RouterCredentialReprotectService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await RouterCredentialReprotector.RunAsync(
                routers,
                protector,
                (router, reason) => logger.LogWarning(
                    "Router credential for router {RouterName} could not be re-protected: {Reason}",
                    router.Name,
                    reason),
                stoppingToken).ConfigureAwait(false);
            if (result.Reprotected > 0 || result.Failed > 0)
            {
                logger.LogInformation(
                    "Router credential re-protect finished. Reprotected={Reprotected}; Failed={Failed}.",
                    result.Reprotected,
                    result.Failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Router credential re-protect failed; legacy credentials remain readable and it will retry on the next start.");
        }
    }
}
