using Microsoft.EntityFrameworkCore;
using Viegard.Application.Stores;
using Viegard.Domain.Admin;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresAdminErrorStore(IDbContextFactory<ViegardDbContext> factory) : IAdminErrorStore
{
    public async ValueTask AddAsync(AdminError error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AdminErrors.Add(error.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Self-managing cap: keep only the newest rows so the table never
        // needs retention configuration.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM admin_errors
            WHERE id IN (
                SELECT id FROM admin_errors
                ORDER BY occurred_at DESC, id DESC
                OFFSET {AdminError.KeepNewest}
            );
            """, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AdminError>> ListRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AdminErrors.AsNoTracking()
            .OrderByDescending(row => row.OccurredAt)
            .ThenByDescending(row => row.Id)
            .Take(Math.Max(1, take))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async ValueTask<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.AdminErrors.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
