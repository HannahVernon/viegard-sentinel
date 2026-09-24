using Microsoft.Extensions.Options;
using Viegard.Application.Doh;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Periodically fetches the curated DoH IPv4 feeds (dibdot primary, optional
/// secondary), merges and de-duplicates them, and replaces the desired-address
/// snapshot.  The feeds are the authority for inclusion; the probe only annotates
/// confirmation state.
/// </summary>
public sealed class DohFeedFetchWorker(
    IDohBlocklistSettingsStore settingsStore,
    IDohDesiredAddressStore desiredAddresses,
    HttpClient httpClient,
    IOptions<DohBlocklistOptions> options,
    TimeProvider timeProvider,
    ILogger<DohFeedFetchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DoH feed fetch worker started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var cycle = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                if (!cycle.Succeeded && !cycle.Skipped)
                {
                    logger.LogWarning("DoH feed fetch failed: {Detail}", cycle.Detail);
                }

                await DelayAsync(cycle.EffectiveFetchInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("DoH feed fetch worker stopping.");
    }

    internal async Task<DohFeedFetchCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? DohBlocklistSettings.FromOptions(options.Value, now);

        if (!settings.Enabled)
        {
            logger.LogInformation("DoH feed fetch skipped because the feature is disabled.");
            return DohFeedFetchCycleResult.CreateSkipped(settings.FetchInterval);
        }

        var feedUrls = new List<string> { settings.PrimaryFeedUrl };
        if (!string.IsNullOrWhiteSpace(settings.SecondaryFeedUrl))
        {
            feedUrls.Add(settings.SecondaryFeedUrl);
        }

        var merged = new HashSet<string>(StringComparer.Ordinal);
        var anySucceeded = false;
        string? firstFailure = null;
        foreach (var feedUrl in feedUrls)
        {
            var (addresses, detail) = await FetchFeedAsync(feedUrl, cancellationToken).ConfigureAwait(false);
            if (detail is not null)
            {
                firstFailure ??= detail;
                logger.LogWarning("DoH feed {FeedUrl} could not be fetched or parsed: {Detail}", feedUrl, detail);
                continue;
            }

            anySucceeded = true;
            foreach (var address in addresses)
            {
                merged.Add(address);
            }
        }

        if (!anySucceeded)
        {
            return DohFeedFetchCycleResult.Failed(settings.FetchInterval, firstFailure ?? "No DoH feed could be fetched.");
        }

        var update = await desiredAddresses
            .ReplaceSnapshotAsync(merged, now, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "DoH feed snapshot applied. Added={Added}; Updated={Updated}; Removed={Removed}; CurrentCount={CurrentCount}.",
            update.Added,
            update.Updated,
            update.Removed,
            update.CurrentCount);
        return DohFeedFetchCycleResult.Success(
            settings.FetchInterval,
            update.Added,
            update.Updated,
            update.Removed,
            update.CurrentCount);
    }

    private async Task<(IReadOnlyList<string> Addresses, string? Detail)> FetchFeedAsync(
        string feedUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(feedUrl, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ([], $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            return (DohFeedParser.ParseText(payload), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ([], "DoH feed request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return ([], ex.Message);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record DohFeedFetchCycleResult(
    bool Succeeded,
    bool Skipped,
    TimeSpan EffectiveFetchInterval,
    string? Detail,
    int Added,
    int Updated,
    int Removed,
    int CurrentCount)
{
    public static DohFeedFetchCycleResult CreateSkipped(TimeSpan interval) =>
        new(true, true, interval, null, 0, 0, 0, 0);

    public static DohFeedFetchCycleResult Failed(TimeSpan interval, string detail) =>
        new(false, false, interval, detail, 0, 0, 0, 0);

    public static DohFeedFetchCycleResult Success(
        TimeSpan interval,
        int added,
        int updated,
        int removed,
        int currentCount) =>
        new(true, false, interval, null, added, updated, removed, currentCount);
}
