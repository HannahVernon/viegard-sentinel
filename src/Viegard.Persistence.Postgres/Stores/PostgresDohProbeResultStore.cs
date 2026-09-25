using Microsoft.EntityFrameworkCore;
using Viegard.Application.Doh;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresDohProbeResultStore(IDbContextFactory<ViegardDbContext> factory) : IDohProbeResultStore
{
    public async ValueTask<IReadOnlyList<DohProbeResult>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.DohProbeResults
            .AsNoTracking()
            .OrderBy(row => row.Address)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async ValueTask<IReadOnlyList<DohProbeStatusCount>> CountByStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var grouped = await db.DohProbeResults
            .AsNoTracking()
            .GroupBy(row => new { row.Status, row.HttpStatus })
            .Select(group => new { group.Key.Status, group.Key.HttpStatus, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return grouped
            .Select(item => new DohProbeStatusCount((DohProbeStatus)item.Status, item.HttpStatus, item.Count))
            .ToList();
    }

    public async ValueTask SaveAsync(DohProbeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.DohProbeResults
            .FirstOrDefaultAsync(row => row.Address == result.Address, cancellationToken)
            .ConfigureAwait(false);
        var row = result.ToRow();
        if (existing is null)
        {
            db.DohProbeResults.Add(row);
        }
        else
        {
            existing.Status = row.Status;
            existing.HttpStatus = row.HttpStatus;
            existing.TokenMatched = row.TokenMatched;
            existing.ConsecutiveFailures = row.ConsecutiveFailures;
            existing.FirstSeenAt = row.FirstSeenAt;
            existing.LastProbedAt = row.LastProbedAt;
            existing.LastConfirmedAt = row.LastConfirmedAt;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> PruneAsync(
        IReadOnlyCollection<string> keepAddresses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keepAddresses);
        var keep = keepAddresses.ToHashSet(StringComparer.Ordinal);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.DohProbeResults.ToListAsync(cancellationToken).ConfigureAwait(false);
        var stale = rows.Where(row => !keep.Contains(row.Address)).ToList();
        if (stale.Count > 0)
        {
            db.DohProbeResults.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return stale.Count;
    }
}
