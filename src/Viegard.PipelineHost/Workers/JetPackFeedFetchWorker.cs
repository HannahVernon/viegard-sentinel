using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class JetPackFeedFetchWorker(
    IJetPackFeedSettingsStore settingsStore,
    IJetPackDesiredAddressStore desiredAddresses,
    HttpClient httpClient,
    IOptions<JetPackFeedOptions> options,
    TimeProvider timeProvider,
    ILogger<JetPackFeedFetchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JetPack feed fetch worker started.");

        try
        {
            await settingsStore
                .SeedIfMissingAsync(options.Value, timeProvider.GetUtcNow(), stoppingToken)
                .ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                var cycle = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                if (!cycle.Succeeded && !cycle.Skipped)
                {
                    logger.LogWarning("JetPack feed fetch failed: {Detail}", cycle.Detail);
                }

                await DelayAsync(cycle.EffectiveFetchInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("JetPack feed fetch worker stopping.");
    }

    internal async Task<JetPackFeedFetchCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? JetPackFeedSettings.FromOptions(options.Value, now);

        if (!settings.Enabled)
        {
            logger.LogInformation("JetPack feed fetch skipped because the feed is disabled.");
            return JetPackFeedFetchCycleResult.CreateSkipped(settings.FetchInterval);
        }

        try
        {
            using var response = await httpClient.GetAsync(settings.FeedUrl, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return JetPackFeedFetchCycleResult.Failed(
                    settings.FetchInterval,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            if (!TryParseAddresses(payload, out var parsedAddresses, out var detail))
            {
                logger.LogWarning("JetPack feed payload could not be parsed: {Detail}", detail);
                return JetPackFeedFetchCycleResult.Failed(settings.FetchInterval, detail);
            }

            var update = await desiredAddresses
                .ReplaceSnapshotAsync(parsedAddresses, now, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "JetPack feed snapshot applied. Added={Added}; Updated={Updated}; Removed={Removed}; CurrentCount={CurrentCount}; FeedUrl={FeedUrl}.",
                update.Added,
                update.Updated,
                update.Removed,
                update.CurrentCount,
                settings.FeedUrl);
            return JetPackFeedFetchCycleResult.Success(
                settings.FetchInterval,
                update.Added,
                update.Updated,
                update.Removed,
                update.CurrentCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return JetPackFeedFetchCycleResult.Failed(settings.FetchInterval, "JetPack feed request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return JetPackFeedFetchCycleResult.Failed(settings.FetchInterval, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return JetPackFeedFetchCycleResult.Failed(settings.FetchInterval, ex.Message);
        }
        catch (JsonException ex)
        {
            return JetPackFeedFetchCycleResult.Failed(settings.FetchInterval, ex.Message);
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

    internal static bool TryParseAddresses(
        string payload,
        out IReadOnlyList<string> addresses,
        out string detail)
    {
        addresses = [];
        detail = string.Empty;

        using var document = JsonDocument.Parse(payload);
        if (TryReadArray(document.RootElement, out var parsed, out detail))
        {
            addresses = parsed;
            return true;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array
                    && TryReadArray(property.Value, out parsed, out detail))
                {
                    addresses = parsed;
                    return true;
                }
            }

            detail = "JetPack feed JSON object did not contain a string-array property.";
            return false;
        }

        detail = "JetPack feed JSON was neither a string array nor an object containing one.";
        return false;
    }

    private static bool TryReadArray(
        JsonElement element,
        out IReadOnlyList<string> addresses,
        out string detail)
    {
        addresses = [];
        detail = string.Empty;
        if (element.ValueKind != JsonValueKind.Array)
        {
            detail = "JetPack feed JSON element was not an array.";
            return false;
        }

        var parsed = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                detail = "JetPack feed array contained a non-string item.";
                return false;
            }

            if (!JetPackDesiredAddressValidator.TryNormalizeAddress(item.GetString(), out var address, out detail))
            {
                return false;
            }

            parsed.Add(address);
        }

        addresses = parsed;
        return true;
    }
}

public sealed record JetPackFeedFetchCycleResult(
    bool Succeeded,
    bool Skipped,
    TimeSpan EffectiveFetchInterval,
    string? Detail,
    int Added,
    int Updated,
    int Removed,
    int CurrentCount)
{
    public static JetPackFeedFetchCycleResult CreateSkipped(TimeSpan interval) =>
        new(true, true, interval, null, 0, 0, 0, 0);

    public static JetPackFeedFetchCycleResult Failed(TimeSpan interval, string detail) =>
        new(false, false, interval, detail, 0, 0, 0, 0);

    public static JetPackFeedFetchCycleResult Success(
        TimeSpan interval,
        int added,
        int updated,
        int removed,
        int currentCount) =>
        new(true, false, interval, null, added, updated, removed, currentCount);
}
