using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres;

/// <summary>
/// Translates the domain's descriptor strings (source, classifier, policy,
/// action provider) to and from the surrogate keys of the insert-only
/// reference tables (D-0031).  Reference rows are never deleted and their
/// natural keys never change, so both lookup directions are cached for the
/// process lifetime; after warmup a resolution costs a dictionary hit.
/// Concurrent first-inserts race benignly: the loser of the unique-index
/// race re-reads the winner's row.
/// </summary>
public sealed class ReferenceResolver(IDbContextFactory<ViegardDbContext> factory)
{
    private const string UniqueViolation = "23505";

    private readonly ConcurrentDictionary<string, int> _sourceIdsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, (string Key, string? Type)> _sourcesById = new();
    private readonly ConcurrentDictionary<string, int> _classifierIdsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _classifiersById = new();
    private readonly ConcurrentDictionary<(string Key, string Version), int> _policyIdsByKey = new();
    private readonly ConcurrentDictionary<int, (string Key, string Version)> _policiesById = new();
    private readonly ConcurrentDictionary<string, int> _providerIdsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _providersById = new();

    /// <summary>
    /// Resolves a source key to its reference id, creating the row on first
    /// sight.  <paramref name="sourceType"/> may be null when the caller
    /// does not know the type (audit); a known type fills a null column
    /// exactly once and never rewrites an existing value.
    /// </summary>
    public async ValueTask<int> ResolveSourceAsync(string sourceKey, string? sourceType, CancellationToken cancellationToken = default)
    {
        if (_sourceIdsByKey.TryGetValue(sourceKey, out var cached))
        {
            // Fill-once: upgrade a null type if this caller knows it.
            if (sourceType is not null && _sourcesById.TryGetValue(cached, out var entry) && entry.Type is null)
            {
                await FillSourceTypeAsync(cached, sourceKey, sourceType, cancellationToken).ConfigureAwait(false);
            }

            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Sources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SourceKey == sourceKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var row = new SourceRow { SourceKey = sourceKey, SourceType = sourceType, FirstSeenAt = DateTimeOffset.UtcNow };
            try
            {
                db.Sources.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                existing = row;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                // Another process inserted the same key first; use its row.
                db.ChangeTracker.Clear();
                existing = await db.Sources.AsNoTracking()
                    .FirstAsync(s => s.SourceKey == sourceKey, cancellationToken).ConfigureAwait(false);
            }
        }

        if (existing.SourceType is null && sourceType is not null)
        {
            await FillSourceTypeAsync(existing.Id, sourceKey, sourceType, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Cache(existing.Id, sourceKey, existing.SourceType);
        }

        return _sourceIdsByKey[sourceKey];
    }

    /// <summary>Reverse lookup for reads; the foreign key guarantees the row exists.</summary>
    public async ValueTask<(string Key, string? Type)> GetSourceAsync(int id, CancellationToken cancellationToken = default)
    {
        if (_sourcesById.TryGetValue(id, out var cached) && cached.Type is not null)
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Sources.AsNoTracking().FirstAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        Cache(row.Id, row.SourceKey, row.SourceType);
        return (row.SourceKey, row.SourceType);
    }

    public async ValueTask<int> ResolveClassifierAsync(string classifierKey, CancellationToken cancellationToken = default)
    {
        if (_classifierIdsByKey.TryGetValue(classifierKey, out var cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Classifiers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClassifierKey == classifierKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var row = new ClassifierRow { ClassifierKey = classifierKey, FirstSeenAt = DateTimeOffset.UtcNow };
            try
            {
                db.Classifiers.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                existing = row;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                existing = await db.Classifiers.AsNoTracking()
                    .FirstAsync(c => c.ClassifierKey == classifierKey, cancellationToken).ConfigureAwait(false);
            }
        }

        _classifierIdsByKey[classifierKey] = existing.Id;
        _classifiersById[existing.Id] = classifierKey;
        return existing.Id;
    }

    public async ValueTask<string> GetClassifierAsync(int id, CancellationToken cancellationToken = default)
    {
        if (_classifiersById.TryGetValue(id, out var cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Classifiers.AsNoTracking().FirstAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        _classifierIdsByKey[row.ClassifierKey] = row.Id;
        _classifiersById[row.Id] = row.ClassifierKey;
        return row.ClassifierKey;
    }

    public async ValueTask<int> ResolvePolicyAsync(string policyKey, string policyVersion, CancellationToken cancellationToken = default)
    {
        if (_policyIdsByKey.TryGetValue((policyKey, policyVersion), out var cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Policies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PolicyKey == policyKey && p.PolicyVersion == policyVersion, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            var row = new PolicyRow { PolicyKey = policyKey, PolicyVersion = policyVersion, FirstSeenAt = DateTimeOffset.UtcNow };
            try
            {
                db.Policies.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                existing = row;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                existing = await db.Policies.AsNoTracking()
                    .FirstAsync(p => p.PolicyKey == policyKey && p.PolicyVersion == policyVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _policyIdsByKey[(policyKey, policyVersion)] = existing.Id;
        _policiesById[existing.Id] = (policyKey, policyVersion);
        return existing.Id;
    }

    public async ValueTask<(string Key, string Version)> GetPolicyAsync(int id, CancellationToken cancellationToken = default)
    {
        if (_policiesById.TryGetValue(id, out var cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Policies.AsNoTracking().FirstAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        _policyIdsByKey[(row.PolicyKey, row.PolicyVersion)] = row.Id;
        _policiesById[row.Id] = (row.PolicyKey, row.PolicyVersion);
        return (row.PolicyKey, row.PolicyVersion);
    }

    public async ValueTask<int> ResolveActionProviderAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        if (_providerIdsByKey.TryGetValue(providerKey, out var cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.ActionProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderKey == providerKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var row = new ActionProviderRow { ProviderKey = providerKey, FirstSeenAt = DateTimeOffset.UtcNow };
            try
            {
                db.ActionProviders.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                existing = row;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                existing = await db.ActionProviders.AsNoTracking()
                    .FirstAsync(p => p.ProviderKey == providerKey, cancellationToken).ConfigureAwait(false);
            }
        }

        _providerIdsByKey[providerKey] = existing.Id;
        _providersById[existing.Id] = providerKey;
        return existing.Id;
    }

    public async ValueTask<string> GetActionProviderAsync(int id, CancellationToken cancellationToken = default)
    {
        if (_providersById.TryGetValue(id, out var cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ActionProviders.AsNoTracking().FirstAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        _providerIdsByKey[row.ProviderKey] = row.Id;
        _providersById[row.Id] = row.ProviderKey;
        return row.ProviderKey;
    }

    /// <summary>Atomically fills a null source_type; a concurrent different fill wins and is re-read.</summary>
    private async Task FillSourceTypeAsync(int id, string sourceKey, string sourceType, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Sources
            .Where(s => s.Id == id && s.SourceType == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceType, sourceType), cancellationToken)
            .ConfigureAwait(false);
        var current = await db.Sources.AsNoTracking().FirstAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        Cache(current.Id, sourceKey, current.SourceType);
    }

    private void Cache(int id, string key, string? type)
    {
        _sourceIdsByKey[key] = id;
        _sourcesById[id] = (key, type);
    }
}
