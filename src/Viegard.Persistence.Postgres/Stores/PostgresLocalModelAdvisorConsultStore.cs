using Microsoft.EntityFrameworkCore;
using Viegard.Application.Configuration;
using Viegard.Application.Stores;

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

    public async Task<AdvisorConsultRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.LocalModelAdvisorConsults.AsNoTracking()
                .Where(r => r.Id == id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
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

    public async Task<KeysetPage<AdvisorConsultRecord>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        AdvisorConsultOutcome? outcome,
        CancellationToken cancellationToken = default)
    {
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var take = safePageSize + 1;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        FormattableString query = (beforeId, outcome) switch
        {
            (null, null) =>
                $"SELECT * FROM local_model_advisor_consults ORDER BY id DESC LIMIT {take}",
            (null, not null) =>
                $"SELECT * FROM local_model_advisor_consults WHERE outcome = {(int)outcome.Value} ORDER BY id DESC LIMIT {take}",
            (not null, null) =>
                $"SELECT * FROM local_model_advisor_consults WHERE id < {beforeId.Value} ORDER BY id DESC LIMIT {take}",
            (not null, not null) =>
                $"SELECT * FROM local_model_advisor_consults WHERE id < {beforeId.Value} AND outcome = {(int)outcome.Value} ORDER BY id DESC LIMIT {take}",
        };
        var rows = await db.LocalModelAdvisorConsults.FromSqlInterpolated(query)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = rows.Take(safePageSize).Select(r => r.ToDomain()).ToList();
        var nextCursor = rows.Count > safePageSize && items.Count > 0 ? items[^1].Id : (Guid?)null;
        var totalCount = outcome is null
            ? await db.LocalModelAdvisorConsults.LongCountAsync(cancellationToken).ConfigureAwait(false)
            : await db.LocalModelAdvisorConsults.LongCountAsync(r => r.Outcome == (int)outcome.Value, cancellationToken).ConfigureAwait(false);
        var preceding = items.Count == 0
            ? 0
            : outcome is null
                ? await db.LocalModelAdvisorConsults.LongCountAsync(r => r.Id.CompareTo(items[0].Id) > 0, cancellationToken).ConfigureAwait(false)
                : await db.LocalModelAdvisorConsults.LongCountAsync(r => r.Outcome == (int)outcome.Value && r.Id.CompareTo(items[0].Id) > 0, cancellationToken).ConfigureAwait(false);
        return new KeysetPage<AdvisorConsultRecord>(items, nextCursor, totalCount, preceding);
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

    public async Task<long> GetCacheHitCountAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.LocalModelAdvisorConsults.AsNoTracking()
            .LongCountAsync(r => r.CreatedAt >= cutoff && r.ServedFromCache, cancellationToken)
            .ConfigureAwait(false);
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
