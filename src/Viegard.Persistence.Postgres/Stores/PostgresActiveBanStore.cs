using Microsoft.EntityFrameworkCore;
using Viegard.Application.Actions;
using Viegard.Domain.Actions;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresActiveBanStore(IDbContextFactory<ViegardDbContext> factory) : IActiveBanStore
{
    public async ValueTask<ActiveBan> UpsertByIpAsync(
        ActiveBan activeBan,
        CancellationToken cancellationToken = default)
    {
        var normalized = ActiveBan.NormalizeForSave(activeBan);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO active_bans (id, ip, expires_at, created_at, decision_id, action_id)
            VALUES ({normalized.Id}, {normalized.Ip}, {normalized.ExpiresAt}, {normalized.CreatedAt}, {normalized.DecisionId}, {normalized.ActionId})
            ON CONFLICT (ip) DO UPDATE
            SET id = EXCLUDED.id,
                expires_at = EXCLUDED.expires_at,
                created_at = EXCLUDED.created_at,
                decision_id = EXCLUDED.decision_id,
                action_id = EXCLUDED.action_id
            """, cancellationToken).ConfigureAwait(false);

        return (await GetByIpAsync(normalized.Ip, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidOperationException("Active ban upsert did not return the saved row.");
    }

    public async ValueTask<bool> RemoveByIpAsync(string ip, CancellationToken cancellationToken = default)
    {
        var normalizedIp = ActiveBan.CanonicalizeIp(ip);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await db.ActiveBans
            .Where(row => row.Ip == normalizedIp)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    public async ValueTask<IReadOnlyList<ActiveBan>> ListUnexpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var cutoff = now.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ActiveBans
            .AsNoTracking()
            .Where(row => row.ExpiresAt > cutoff)
            .OrderBy(row => row.Ip)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async ValueTask<long> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ActiveBans
            .Where(row => row.ExpiresAt <= cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ActiveBan?> GetByIpAsync(string ip, CancellationToken cancellationToken = default)
    {
        var normalizedIp = ActiveBan.CanonicalizeIp(ip);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.ActiveBans
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Ip == normalizedIp, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }
}
