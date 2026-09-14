using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Application.Audit;
using Viegard.Application.Retention;
using Viegard.Domain;
using Viegard.Domain.Audit;

namespace Viegard.PipelineHost.Workers;

public sealed class RetentionWorker(
    IRetentionStore retentionStore,
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
        var configuredPeriods = retentionOptions.ConfiguredPeriods();
        if (configuredPeriods.Count == 0)
        {
            logger.LogDebug("Retention purge cycle skipped because no retention periods are configured.");
            return new RetentionCycleResult(0, []);
        }

        var now = timeProvider.GetUtcNow();
        var batchSize = retentionOptions.EffectiveBatchSize;
        var results = new List<RetentionTargetResult>(configuredPeriods.Count);

        foreach (var period in configuredPeriods)
        {
            var cutoff = now.AddDays(-period.Days);
            var rowsDeleted = await retentionStore
                .PurgeAsync(period.Target, cutoff, batchSize, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new RetentionTargetResult(period.Target.TableName(), period.Days, cutoff, rowsDeleted));
        }

        var totalDeleted = results.Sum(r => r.RowsDeleted);
        if (totalDeleted == 0)
        {
            logger.LogDebug(
                "Retention purge cycle completed with no rows removed. ConfiguredTargets={ConfiguredTargets}; BatchSize={BatchSize}.",
                results.Count,
                batchSize);
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
