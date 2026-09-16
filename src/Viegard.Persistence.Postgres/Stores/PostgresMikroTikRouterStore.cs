using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresMikroTikRouterStore(IDbContextFactory<ViegardDbContext> factory) : IMikroTikRouterStore
{
    private const string UniqueViolation = "23505";

    public async ValueTask<IReadOnlyList<MikroTikRouter>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.MikroTikRouters
            .AsNoTracking()
            .OrderBy(router => router.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async ValueTask<MikroTikRouter?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.MikroTikRouters
                .AsNoTracking()
                .FirstOrDefaultAsync(router => router.Id == id, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }

    public async ValueTask<MikroTikRouterSaveResult> CreateAsync(
        MikroTikRouter router,
        string passwordCiphertext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        ValidatePasswordCiphertext(passwordCiphertext);
        var normalized = MikroTikRouterValidator.NormalizeForSave(router) with { RowVersion = 1 };
        var row = normalized.ToRow();
        row.PasswordCiphertext = passwordCiphertext;

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.MikroTikRouters.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return MikroTikRouterSaveResult.Saved(normalized);
        }
        catch (DbUpdateException ex) when (IsNameUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return MikroTikRouterSaveResult.DuplicateName(
                await GetByNameAsync(db, normalized.Name, cancellationToken).ConfigureAwait(false));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return MikroTikRouterSaveResult.Conflict(
                await GetAsync(normalized.Id, cancellationToken).ConfigureAwait(false));
        }
    }

    public async ValueTask<MikroTikRouterSaveResult> UpdateAsync(
        MikroTikRouter router,
        int expectedRowVersion,
        string? passwordCiphertext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        if (passwordCiphertext is not null)
        {
            ValidatePasswordCiphertext(passwordCiphertext);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var current = await db.MikroTikRouters
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == router.Id, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return MikroTikRouterSaveResult.NotFound();
        }

        var normalized = MikroTikRouterValidator.NormalizeForSave(router) with
        {
            CreatedAt = current.CreatedAt,
        };

        try
        {
            var updated = passwordCiphertext is null
                ? await UpdateWithoutPasswordAsync(db, normalized, expectedRowVersion, cancellationToken).ConfigureAwait(false)
                : await UpdateWithPasswordAsync(db, normalized, expectedRowVersion, passwordCiphertext, cancellationToken).ConfigureAwait(false);

            if (updated != 1)
            {
                return MikroTikRouterSaveResult.Conflict(
                    await GetAsync(router.Id, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return MikroTikRouterSaveResult.DuplicateName(
                await GetByNameAsync(db, normalized.Name, cancellationToken).ConfigureAwait(false));
        }
        catch (PostgresException ex) when (IsNameUniqueViolation(ex))
        {
            return MikroTikRouterSaveResult.DuplicateName(
                await GetByNameAsync(db, normalized.Name, cancellationToken).ConfigureAwait(false));
        }

        return MikroTikRouterSaveResult.Saved(
            (await GetAsync(router.Id, cancellationToken).ConfigureAwait(false))!);
    }

    public async ValueTask<MikroTikRouterDeleteResult> DeleteAsync(
        Guid id,
        int expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await db.MikroTikRouters
            .Where(row => row.Id == id && row.RowVersion == expectedRowVersion)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (deleted == 1)
        {
            return MikroTikRouterDeleteResult.Deleted();
        }

        var current = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        return current is null
            ? MikroTikRouterDeleteResult.NotFound()
            : MikroTikRouterDeleteResult.Conflict(current);
    }

    public async ValueTask<string?> GetCredentialCiphertextAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.MikroTikRouters
            .AsNoTracking()
            .Where(row => row.Id == id)
            .Select(row => row.PasswordCiphertext)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<int> UpdateWithoutPasswordAsync(
        ViegardDbContext db,
        MikroTikRouter router,
        int expectedRowVersion,
        CancellationToken cancellationToken) =>
        await db.MikroTikRouters
            .Where(row => row.Id == router.Id && row.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Name, router.Name)
                .SetProperty(row => row.BaseUrl, router.BaseUrl)
                .SetProperty(row => row.TransportMode, router.TransportMode.ToString())
                .SetProperty(row => row.PinnedCertificateSha256, router.PinnedCertificateSha256)
                .SetProperty(row => row.Username, router.Username)
                .SetProperty(row => row.Enabled, router.Enabled)
                .SetProperty(row => row.UpdatedAt, router.UpdatedAt.ToUniversalTime())
                .SetProperty(row => row.UpdatedBy, router.UpdatedBy)
                .SetProperty(row => row.RowVersion, expectedRowVersion + 1),
                cancellationToken)
            .ConfigureAwait(false);

    private static async ValueTask<int> UpdateWithPasswordAsync(
        ViegardDbContext db,
        MikroTikRouter router,
        int expectedRowVersion,
        string passwordCiphertext,
        CancellationToken cancellationToken) =>
        await db.MikroTikRouters
            .Where(row => row.Id == router.Id && row.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Name, router.Name)
                .SetProperty(row => row.BaseUrl, router.BaseUrl)
                .SetProperty(row => row.TransportMode, router.TransportMode.ToString())
                .SetProperty(row => row.PinnedCertificateSha256, router.PinnedCertificateSha256)
                .SetProperty(row => row.Username, router.Username)
                .SetProperty(row => row.PasswordCiphertext, passwordCiphertext)
                .SetProperty(row => row.Enabled, router.Enabled)
                .SetProperty(row => row.UpdatedAt, router.UpdatedAt.ToUniversalTime())
                .SetProperty(row => row.UpdatedBy, router.UpdatedBy)
                .SetProperty(row => row.RowVersion, expectedRowVersion + 1),
                cancellationToken)
            .ConfigureAwait(false);

    private static async ValueTask<MikroTikRouter?> GetByNameAsync(
        ViegardDbContext db,
        string name,
        CancellationToken cancellationToken) =>
        (await db.MikroTikRouters
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Name == name, cancellationToken)
            .ConfigureAwait(false))
        ?.ToDomain();

    private static void ValidatePasswordCiphertext(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Router password ciphertext is required.");
        }
    }

    private static bool IsNameUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres && IsNameUniqueViolation(postgres);

    private static bool IsNameUniqueViolation(PostgresException exception) =>
        exception.SqlState == UniqueViolation
        && string.Equals(exception.ConstraintName, "IX_mikrotik_routers_name", StringComparison.Ordinal);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
