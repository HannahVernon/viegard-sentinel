using System.Reflection;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.AdminApi;

public sealed class AdminInstanceRegistrationService(
    IInstanceRegistryStore registryStore,
    ILogger<AdminInstanceRegistrationService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly BuildVersionInfo _buildVersion =
        BuildVersion.FromAssembly(Assembly.GetEntryAssembly() ?? typeof(AdminInstanceRegistrationService).Assembly);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var instanceId = "admin-" + Environment.MachineName.ToLowerInvariant();
        await UpsertRegistrationAsync(instanceId, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await UpsertRegistrationAsync(instanceId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task UpsertRegistrationAsync(string instanceId, CancellationToken cancellationToken)
    {
        try
        {
            await registryStore.UpsertAsync(
                new InstanceRegistration
                {
                    InstanceId = instanceId,
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
                instanceId);
        }
    }
}
