using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Application.Audit;
using Viegard.Application.Retention;
using Viegard.Domain;
using Viegard.Domain.Audit;

namespace Viegard.PipelineHost.Workers;

public sealed class RetentionWorker(
    IRetentionStore retentionStore,
    IRetentionSettingsStore retentionSettingsStore,
    IAuditLedger auditLedger,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionOptions = options.Value;
        logger.LogInformation(
            "Retention worker started. StartupDelay={StartupDelay}; CheckInterval={CheckInterval}; BatchSize={BatchSize}.",
            retentionOptions.StartupDelay,
            retentionOptions.CheckInterval,
            retentionOptions.EffectiveBatchSize);

        try
        {
            await retentionSettingsStore
                .SeedIfMissingAsync(retentionOptions, timeProvider.GetUtcNow(), stoppingToken)
                .ConfigureAwait(false);

            await DelayAsync(retentionOptions.StartupDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Retention worker failed during a purge cycle.");
                }

                await DelayAsync(options.Value.CheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("Retention worker stopping.");
    }

    public async Task<RetentionCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var retentionOptions = options.Value;
        var now = timeProvider.GetUtcNow();
        var settings = await retentionSettingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            logger.LogDebug("Retention purge cycle skipped because retention settings have not been seeded.");
            await retentionSettingsStore
                .UpdateLastCycleAsync(now, RetentionSettings.EmptyCounts(), cancellationToken)
                .ConfigureAwait(false);
            return new RetentionCycleResult(0, []);
        }

        var configuredPeriods = settings.ConfiguredPeriods();
        var cycleCounts = RetentionSettings.EmptyCounts().ToDictionary(pair => pair.Key, pair => pair.Value);
        if (configuredPeriods.Count == 0)
        {
            logger.LogDebug("Retention purge cycle skipped because no retention periods are configured.");
            await retentionSettingsStore
                .UpdateLastCycleAsync(now, cycleCounts, cancellationToken)
                .ConfigureAwait(false);
            return new RetentionCycleResult(0, []);
        }

        var batchSize = retentionOptions.EffectiveBatchSize;
        var results = new List<RetentionTargetResult>(configuredPeriods.Count);

        foreach (var period in configuredPeriods)
        {
            var cutoff = now.AddDays(-period.Days);
            var rowsDeleted = await retentionStore
                .PurgeAsync(period.Target, cutoff, batchSize, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new RetentionTargetResult(period.Target.TableName(), period.Days, cutoff, rowsDeleted));
            cycleCounts[period.Target] = rowsDeleted;
        }

        var totalDeleted = results.Sum(r => r.RowsDeleted);
        if (totalDeleted == 0)
        {
            logger.LogDebug(
                "Retention purge cycle completed with no rows removed. ConfiguredTargets={ConfiguredTargets}; BatchSize={BatchSize}.",
                results.Count,
                batchSize);
            await retentionSettingsStore
                .UpdateLastCycleAsync(now, cycleCounts, cancellationToken)
                .ConfigureAwait(false);
            return new RetentionCycleResult(0, results);
        }

        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = ViegardId.New(),
            Timestamp = now,
            Stage = PipelineStage.System,
            Summary = $"Retention purge removed {totalDeleted} rows.",
            DetailJson = DetailJson(results, batchSize),
        }, cancellationToken).ConfigureAwait(false);

        var counts = results
            .Where(r => r.RowsDeleted > 0)
            .ToDictionary(r => r.TableName, r => r.RowsDeleted, StringComparer.Ordinal);
        logger.LogInformation(
            "Retention purge cycle removed {RowsDeleted} row(s). Counts={RetentionCounts}; BatchSize={BatchSize}.",
            totalDeleted,
            counts,
            batchSize);

        await retentionSettingsStore
            .UpdateLastCycleAsync(now, cycleCounts, cancellationToken)
            .ConfigureAwait(false);

        return new RetentionCycleResult(totalDeleted, results);
    }

    private static string DetailJson(
        IReadOnlyList<RetentionTargetResult> results,
        int batchSize) =>
        JsonSerializer.Serialize(new
        {
            BatchSize = batchSize,
            Targets = results.Select(r => new
            {
                r.TableName,
                r.Days,
                r.Cutoff,
                r.RowsDeleted,
            }).ToList(),
        });

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record RetentionCycleResult(long TotalDeleted, IReadOnlyList<RetentionTargetResult> Targets);

public sealed record RetentionTargetResult(string TableName, int Days, DateTimeOffset Cutoff, long RowsDeleted);
