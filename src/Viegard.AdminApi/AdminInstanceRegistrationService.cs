using System.Reflection;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.AdminApi;

public sealed class AdminInstanceRegistrationService(
    IInstanceRegistryStore registryStore,
    ILogger<AdminInstanceRegistrationService> logger) : BackgroundService
{
    /// <summary>
    /// Stable instance id for the single admin service.  A container-derived
    /// name would mint a new registry row on every rebuild, leaving dead
    /// rows behind; the stable id updates one row in place.
    /// </summary>
    public const string InstanceId = "admin";

    /// <summary>Registry rows silent for longer than this are deleted.</summary>
    public static readonly TimeSpan StaleRowAge = TimeSpan.FromHours(24);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly BuildVersionInfo _buildVersion =
        BuildVersion.FromAssembly(Assembly.GetEntryAssembly() ?? typeof(AdminInstanceRegistrationService).Assembly);
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await UpsertRegistrationAsync(stoppingToken).ConfigureAwait(false);
        await CleanupStaleRowsAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await UpsertRegistrationAsync(stoppingToken).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow - _lastCleanupAt >= CleanupInterval)
                {
                    await CleanupStaleRowsAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task UpsertRegistrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await registryStore.UpsertAsync(
                new InstanceRegistration
                {
                    InstanceId = InstanceId,
                    Version = _buildVersion.InformationalVersion,
                    CommitSha = _buildVersion.CommitSha,
                    Roles = "admin",
                    HostName = Environment.MachineName,
                    StartedAt = _startedAt,
                    ReportedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not register admin instance '{InstanceId}'; retrying on the next heartbeat.",
                InstanceId);
        }
    }

    private async Task CleanupStaleRowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - StaleRowAge;
            var removed = await registryStore.DeleteStaleAsync(cutoff, cancellationToken).ConfigureAwait(false);
            _lastCleanupAt = DateTimeOffset.UtcNow;
            if (removed > 0)
            {
                logger.LogInformation(
                    "Removed {Count} instance registry row(s) not reported since {Cutoff:u}.",
                    removed,
                    cutoff);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clean up stale instance registry rows; retrying later.");
        }
    }
}
