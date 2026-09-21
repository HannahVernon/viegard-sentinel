using Microsoft.EntityFrameworkCore;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresLocalModelAdvisorInjectionPatternStore(
    IDbContextFactory<ViegardDbContext> factory)
    : ILocalModelAdvisorInjectionPatternStore
{
    public async ValueTask<IReadOnlyList<LocalModelAdvisorInjectionPattern>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.LocalModelAdvisorInjectionPatterns.AsNoTracking()
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async ValueTask<LocalModelAdvisorInjectionPattern?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.LocalModelAdvisorInjectionPatterns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }

    public async ValueTask<LocalModelAdvisorInjectionPattern> CreateAsync(
        string category,
        string pattern,
        string? description,
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var candidate = NormalizeNew(category, pattern, description, createdBy, createdAt);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.LocalModelAdvisorInjectionPatterns.Add(candidate.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    public async ValueTask<LocalModelAdvisorInjectionPatternToggleResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var before = await db.LocalModelAdvisorInjectionPatterns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (before is null)
        {
            return LocalModelAdvisorInjectionPatternToggleResult.NotFound();
        }

        await db.LocalModelAdvisorInjectionPatterns
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Enabled, enabled), cancellationToken)
            .ConfigureAwait(false);
        return LocalModelAdvisorInjectionPatternToggleResult.Updated(before.ToDomain(), before.ToDomain() with { Enabled = enabled });
    }

    public async ValueTask<LocalModelAdvisorInjectionPatternDeleteResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var before = await db.LocalModelAdvisorInjectionPatterns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (before is null)
        {
            return LocalModelAdvisorInjectionPatternDeleteResult.NotFound();
        }

        await db.LocalModelAdvisorInjectionPatterns
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return LocalModelAdvisorInjectionPatternDeleteResult.Deleted(before.ToDomain());
    }

    private static LocalModelAdvisorInjectionPattern NormalizeNew(
        string category,
        string pattern,
        string? description,
        string createdBy,
        DateTimeOffset createdAt)
    {
        if (!LocalModelAdvisorInjectionPatternValidator.TryNormalizeCategory(category, out var normalizedCategory, out var error)
            || !LocalModelAdvisorInjectionPatternValidator.TryNormalizePattern(pattern, out var normalizedPattern, out error)
            || !LocalModelAdvisorInjectionPatternValidator.TryNormalizeDescription(description, out var normalizedDescription, out error)
            || !LocalModelAdvisorInjectionPatternValidator.TryNormalizeCreatedBy(createdBy, out var normalizedCreatedBy, out error))
        {
            throw new InvalidOperationException(error);
        }

        return new LocalModelAdvisorInjectionPattern
        {
            Category = normalizedCategory,
            Pattern = normalizedPattern,
            Description = normalizedDescription,
            Enabled = true,
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = normalizedCreatedBy,
        };
    }
}
