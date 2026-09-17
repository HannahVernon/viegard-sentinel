using System.Reflection;
using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Logs the host topology at startup and emits a periodic heartbeat.
/// </summary>
public sealed class PipelineStartupService(
    IOptions<ViegardHostOptions> options,
    IOptions<HostUpgradeAgentOptions> upgradeAgentOptions,
    IInstanceRegistryStore registryStore,
    ILogger<PipelineStartupService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly BuildVersionInfo _buildVersion =
        BuildVersion.FromAssembly(Assembly.GetEntryAssembly() ?? typeof(PipelineStartupService).Assembly);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = options.Value;
        logger.LogInformation(
            "Viegard pipeline host '{InstanceId}' starting with roles: {Roles}.",
            host.EffectiveInstanceId,
            string.Join(", ", host.Roles));

        await UpsertRegistrationAsync(host, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                logger.LogDebug("Heartbeat from pipeline host '{InstanceId}'.", host.EffectiveInstanceId);
                await UpsertRegistrationAsync(host, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        logger.LogInformation("Viegard pipeline host '{InstanceId}' stopping.", host.EffectiveInstanceId);
    }

    private async Task UpsertRegistrationAsync(ViegardHostOptions host, CancellationToken cancellationToken)
    {
        var upgradeTarget = upgradeAgentOptions.Value.Enabled
            ? HostUpgradeCommandPolicy.NormalizeTarget(upgradeAgentOptions.Value.Target)
            : null;

        try
        {
            await registryStore.UpsertAsync(
                new InstanceRegistration
                {
                    InstanceId = host.EffectiveInstanceId,
                    Version = _buildVersion.InformationalVersion,
                    CommitSha = _buildVersion.CommitSha,
                    Roles = string.Join(",", host.Roles),
                    UpgradeTarget = upgradeTarget,
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
                "Could not register pipeline host instance '{InstanceId}'; retrying on the next heartbeat.",
                host.EffectiveInstanceId);
        }
    }
}
