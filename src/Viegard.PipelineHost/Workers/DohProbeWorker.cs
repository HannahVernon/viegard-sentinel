using Microsoft.Extensions.Options;
using Viegard.Application.Doh;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Wraps the dedicated HTTPS client used for canary DoH probes.  Because probes
/// target resolvers by IP, the presented certificate will not match the IP, so
/// this client tolerates certificate mismatches.  The canary token is public, so
/// no secret is exposed by the relaxed validation.
/// </summary>
public sealed class DohProbeHttpClient(HttpClient client)
{
    public HttpClient Client => client;
}

/// <summary>
/// Periodically probes each candidate DoH address with the canary query and
/// records the outcome.  Confirmation-only: a failed probe never removes an
/// address from the curated set; it only annotates the router entry comment.
/// </summary>
public sealed class DohProbeWorker(
    IDohBlocklistSettingsStore settingsStore,
    IDohDesiredAddressStore desiredAddresses,
    IDohProbeResultStore probeResults,
    IDohProbeSummaryStore probeSummaries,
    DohProbeHttpClient probeClient,
    IDohProbeTrigger probeTrigger,
    IOptions<DohBlocklistOptions> options,
    TimeProvider timeProvider,
    ILogger<DohProbeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DoH probe worker started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                var requested = await probeTrigger.WaitForRequestAsync(interval, stoppingToken).ConfigureAwait(false);
                if (requested)
                {
                    logger.LogInformation("DoH probe cycle requested on demand; running early.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("DoH probe worker stopping.");
    }

    internal async Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? DohBlocklistSettings.FromOptions(options.Value, now);

        if (!settings.Enabled || !settings.ProbeEnabled)
        {
            return settings.ProbeInterval;
        }

        var desired = await desiredAddresses.ListAsync(cancellationToken).ConfigureAwait(false);
        await probeResults.PruneAsync(desired.Select(item => item.Address).ToList(), cancellationToken).ConfigureAwait(false);

        var existingResults = (await probeResults.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(result => result.Address, StringComparer.Ordinal);

        var probeTargets = desired
            .Where(item => !item.Address.Contains('/', StringComparison.Ordinal))
            .Select(item => item.Address)
            .ToList();

        var confirmed = 0;
        var failed = 0;
        using var throttle = new SemaphoreSlim(Math.Max(1, settings.ProbeConcurrency));
        var tasks = probeTargets.Select(async address =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var outcome = await DohCanaryProbe.ProbeAsync(
                    probeClient.Client,
                    address,
                    settings.ProbeEndpointPath,
                    settings.ProbeCanaryFqdn,
                    settings.ProbeExpectedToken,
                    settings.ProbeTimeout,
                    cancellationToken).ConfigureAwait(false);

                existingResults.TryGetValue(address, out var existing);
                var probedAt = timeProvider.GetUtcNow();
                var isConfirmed = outcome.Status == DohProbeStatus.Confirmed;
                var updated = new DohProbeResult
                {
                    Address = address,
                    Status = outcome.Status,
                    HttpStatus = outcome.HttpStatus,
                    TokenMatched = outcome.TokenMatched,
                    ConsecutiveFailures = isConfirmed ? 0 : (existing?.ConsecutiveFailures ?? 0) + 1,
                    FirstSeenAt = existing?.FirstSeenAt ?? probedAt,
                    LastProbedAt = probedAt,
                    LastConfirmedAt = isConfirmed ? probedAt : existing?.LastConfirmedAt,
                };
                await probeResults.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

                if (isConfirmed)
                {
                    Interlocked.Increment(ref confirmed);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var counts = await probeResults.CountByStatusAsync(cancellationToken).ConfigureAwait(false);
        var summary = DohProbeOutcomeSummary.FromCounts(counts, timeProvider.GetUtcNow());
        await probeSummaries.SaveCurrentAsync(summary, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "DoH probe cycle complete. Probed={Probed}; Confirmed={Confirmed}; Unconfirmed={Unconfirmed}; Skipped={Skipped}.",
            probeTargets.Count,
            confirmed,
            failed,
            desired.Count - probeTargets.Count);
        return settings.ProbeInterval;
    }
}
