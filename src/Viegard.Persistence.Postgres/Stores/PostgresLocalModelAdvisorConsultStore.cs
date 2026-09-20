using Microsoft.EntityFrameworkCore;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresLocalModelAdvisorConsultStore(IDbContextFactory<ViegardDbContext> factory) : ILocalModelAdvisorConsultStore
{
    public async Task AppendAsync(AdvisorConsultRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.LocalModelAdvisorConsults.Add(record.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdvisorConsultRecord?> GetByClassificationIdAsync(Guid classificationId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.LocalModelAdvisorConsults.AsNoTracking()
                .Where(r => r.ClassificationId == classificationId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }

    public async Task<IReadOnlyList<AdvisorOutcomeCount>> GetOutcomeCountsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var counts = await db.LocalModelAdvisorConsults.AsNoTracking()
            .Where(r => r.CreatedAt >= cutoff)
            .GroupBy(r => r.Outcome)
            .Select(g => new { Outcome = g.Key, Count = g.LongCount() })
            .OrderBy(r => r.Outcome)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return counts
            .Select(c => new AdvisorOutcomeCount((AdvisorConsultOutcome)c.Outcome, c.Count))
            .ToList();
    }

    public async Task<AdvisorLatencyStats> GetLatencyStatsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var latencies = await db.LocalModelAdvisorConsults.AsNoTracking()
            .Where(r => r.CreatedAt >= cutoff
                && r.LatencyMs != null
                && (r.Outcome == (int)AdvisorConsultOutcome.Escalated || r.Outcome == (int)AdvisorConsultOutcome.NoChange))
            .OrderBy(r => r.LatencyMs)
            .Select(r => r.LatencyMs!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToLatencyStats(latencies);
    }

    public async Task<IReadOnlyList<AdvisorConsultRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.LocalModelAdvisorConsults.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .Select(r => r.ToDomain())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var utc = cutoff.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.LocalModelAdvisorConsults
            .Where(r => r.CreatedAt < utc)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static AdvisorLatencyStats ToLatencyStats(IReadOnlyList<int> latencies)
    {
        if (latencies.Count == 0)
        {
            return new AdvisorLatencyStats(0, null, null);
        }

        return new AdvisorLatencyStats(latencies.Count, Percentile(latencies, 0.50), Percentile(latencies, 0.95));
    }

    private static int Percentile(IReadOnlyList<int> sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}
