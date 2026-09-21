using Microsoft.EntityFrameworkCore;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresLocalModelAdvisorResponseCacheStore(IDbContextFactory<ViegardDbContext> factory)
    : ILocalModelAdvisorResponseCacheStore
{
    public async ValueTask<LocalModelAdvisorResponseCacheEntry?> GetAsync(
        string cacheKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var utc = now.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.LocalModelAdvisorResponseCache.AsNoTracking()
                .Where(r => r.CacheKey == cacheKey && r.ExpiresAt > utc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }

    public async ValueTask SetAsync(
        LocalModelAdvisorResponseCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = entry.ToRow();
        await db.LocalModelAdvisorResponseCache
            .Where(r => r.CacheKey == entry.CacheKey)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        db.LocalModelAdvisorResponseCache.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> PruneExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var utc = now.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.LocalModelAdvisorResponseCache
            .Where(r => r.ExpiresAt <= utc)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
