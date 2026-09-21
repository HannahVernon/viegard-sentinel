using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresLocalModelAdvisorCategoryBandStore(
    IDbContextFactory<ViegardDbContext> factory)
    : ILocalModelAdvisorCategoryBandStore
{
    private const string UniqueViolation = "23505";

    public async ValueTask<LocalModelAdvisorCategoryBand?> GetAsync(
        string category,
        CancellationToken cancellationToken = default)
    {
        if (!LocalModelAdvisorCategoryBandValidator.TryNormalizeCategory(category, out var normalized, out _))
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.LocalModelAdvisorCategoryBands.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Category == normalized, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }

    public async ValueTask<IReadOnlyList<LocalModelAdvisorCategoryBand>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.LocalModelAdvisorCategoryBands.AsNoTracking()
            .OrderBy(r => r.Category)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async ValueTask<LocalModelAdvisorCategoryBandSaveResult> UpsertAsync(
        LocalModelAdvisorCategoryBand band,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!LocalModelAdvisorCategoryBandValidator.TryValidate(band, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalized = Normalize(band);
        var normalizedUpdatedBy = LocalModelAdvisorCategoryBandValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedVersion == 0)
        {
            var inserted = normalized with
            {
                Version = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.LocalModelAdvisorCategoryBands.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return LocalModelAdvisorCategoryBandSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return LocalModelAdvisorCategoryBandSaveResult.Conflict(
                    await GetAsync(inserted.Category, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.LocalModelAdvisorCategoryBands
            .Where(r => r.Category == normalized.Category && r.Version == expectedVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Enabled, normalized.Enabled)
                .SetProperty(r => r.InvokeConfidenceMin, normalized.InvokeConfidenceMin)
                .SetProperty(r => r.InvokeConfidenceMax, normalized.InvokeConfidenceMax)
                .SetProperty(r => r.MaxSeverityDelta, normalized.MaxSeverityDelta)
                .SetProperty(r => r.MaxConfidenceDelta, normalized.MaxConfidenceDelta)
                .SetProperty(r => r.DeEscalationEnabled, normalized.DeEscalationEnabled)
                .SetProperty(r => r.MaxDownwardSeverityDelta, normalized.MaxDownwardSeverityDelta)
                .SetProperty(r => r.MaxDownwardConfidenceDelta, normalized.MaxDownwardConfidenceDelta)
                .SetProperty(r => r.Version, expectedVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return LocalModelAdvisorCategoryBandSaveResult.Conflict(
                await GetAsync(normalized.Category, cancellationToken).ConfigureAwait(false));
        }

        return LocalModelAdvisorCategoryBandSaveResult.Saved(
            (await GetAsync(normalized.Category, cancellationToken).ConfigureAwait(false))!);
    }

    public async ValueTask<LocalModelAdvisorCategoryBandDeleteResult> DeleteAsync(
        string category,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!LocalModelAdvisorCategoryBandValidator.TryNormalizeCategory(category, out var normalized, out _))
        {
            return LocalModelAdvisorCategoryBandDeleteResult.NotFound();
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.LocalModelAdvisorCategoryBands.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Category == normalized, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return LocalModelAdvisorCategoryBandDeleteResult.NotFound();
        }

        var deleted = existing.ToDomain();
        if (existing.Version != expectedVersion)
        {
            return LocalModelAdvisorCategoryBandDeleteResult.Conflict(deleted);
        }

        var rows = await db.LocalModelAdvisorCategoryBands
            .Where(r => r.Category == normalized && r.Version == expectedVersion)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows == 1
            ? LocalModelAdvisorCategoryBandDeleteResult.Deleted(deleted)
            : LocalModelAdvisorCategoryBandDeleteResult.Conflict(
                await GetAsync(normalized, cancellationToken).ConfigureAwait(false));
    }

    private static LocalModelAdvisorCategoryBand Normalize(LocalModelAdvisorCategoryBand band)
    {
        _ = LocalModelAdvisorCategoryBandValidator.TryNormalizeCategory(band.Category, out var category, out var categoryError)
            ? true
            : throw new InvalidOperationException(categoryError);
        return band with { Category = category };
    }
}
