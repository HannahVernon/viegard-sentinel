using Microsoft.EntityFrameworkCore;
using Viegard.Application.Stores;
using Viegard.Domain.Admin;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresAppPasswordStore(IDbContextFactory<ViegardDbContext> factory) : IAppPasswordStore
{
    public async ValueTask CreateAsync(AppPassword appPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appPassword);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AppPasswords.Add(ToRow(appPassword));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AppPassword>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AppPasswords.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToDomain).ToList();
    }

    public async ValueTask<AppPassword?> GetByLookupKeyAsync(string lookupKey, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AppPasswords.AsNoTracking()
            .FirstOrDefaultAsync(row => row.LookupKey == lookupKey, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    public async ValueTask<bool> RevokeAsync(Guid id, Guid userId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.AppPasswords
            .Where(row => row.Id == id && row.UserId == userId && row.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.RevokedAt, revokedAt.ToUniversalTime()),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    public async ValueTask UpdateLastUsedAsync(Guid id, DateTimeOffset lastUsedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.AppPasswords
            .Where(row => row.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.LastUsedAt, lastUsedAt.ToUniversalTime()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static AppPasswordRow ToRow(AppPassword appPassword) => new()
    {
        Id = appPassword.Id,
        UserId = appPassword.UserId,
        Name = appPassword.Name,
        LookupKey = appPassword.LookupKey,
        SecretHash = appPassword.SecretHash,
        CreatedAt = appPassword.CreatedAt.ToUniversalTime(),
        ExpiresAt = appPassword.ExpiresAt?.ToUniversalTime(),
        LastUsedAt = appPassword.LastUsedAt?.ToUniversalTime(),
        RevokedAt = appPassword.RevokedAt?.ToUniversalTime(),
    };

    private static AppPassword ToDomain(AppPasswordRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        Name = row.Name,
        LookupKey = row.LookupKey,
        SecretHash = row.SecretHash,
        CreatedAt = row.CreatedAt,
        ExpiresAt = row.ExpiresAt,
        LastUsedAt = row.LastUsedAt,
        RevokedAt = row.RevokedAt,
    };
}
