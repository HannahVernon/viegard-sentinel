using Microsoft.EntityFrameworkCore;
using Viegard.Application.Doh;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresDohDesiredAddressStore(IDbContextFactory<ViegardDbContext> factory) : IDohDesiredAddressStore
{
    public async ValueTask<IReadOnlyList<DohDesiredAddress>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.DohDesiredAddresses
            .AsNoTracking()
            .OrderBy(row => row.Address)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async ValueTask<DohDesiredAddressRefreshResult> ReplaceSnapshotAsync(
        IReadOnlyCollection<string> addresses,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var utcObservedAt = observedAt.ToUniversalTime();
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            if (!DohAddressValidator.TryNormalizeAddress(address, out var canonical, out var error))
            {
                throw new InvalidOperationException(error);
            }

            normalized.Add(canonical);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingRows = await db.DohDesiredAddresses
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = existingRows.ToDictionary(row => row.Address, StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        foreach (var address in normalized)
        {
            if (existing.TryGetValue(address, out var row))
            {
                row.LastSeenAt = utcObservedAt;
                updated++;
            }
            else
            {
                db.DohDesiredAddresses.Add(new DohDesiredAddressRow
                {
                    Address = address,
                    FirstSeenAt = utcObservedAt,
                    LastSeenAt = utcObservedAt,
                });
                added++;
            }
        }

        var removedRows = existingRows
            .Where(row => !normalized.Contains(row.Address))
            .ToList();
        if (removedRows.Count > 0)
        {
            db.DohDesiredAddresses.RemoveRange(removedRows);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DohDesiredAddressRefreshResult(
            added,
            updated,
            removedRows.Count,
            normalized.Count);
    }
}
