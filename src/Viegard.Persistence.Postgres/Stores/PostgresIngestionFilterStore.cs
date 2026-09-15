using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Configuration;
using Viegard.Application.Stores;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresIngestionFilterStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresIngestionFilterStore>? logger = null)
    : IIngestionFilterStore
{
    private const string NotifyChannel = "viegard_config_ingestion_filters";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<IReadOnlyList<IngestionFilter>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.IngestionFilters.AsNoTracking()
                .OrderBy(f => f.SourceType)
                .ThenBy(f => f.EventKind)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(row => row.ToDomain())
            .ToList();
    }

    public async ValueTask<IReadOnlyList<IngestionFilter>> ListForSourceAsync(
        string sourceType,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourceType = IngestionFilterValidation.NormalizeSourceType(sourceType);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ListForSourceInternalAsync(db, normalizedSourceType, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IngestionFilterSaveResult> SaveMatrixAsync(
        string sourceType,
        IReadOnlyDictionary<string, bool> suppressions,
        IReadOnlySet<string> lockedEventKinds,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        var rows = IngestionFilterValidation.BuildRows(sourceType, suppressions, lockedEventKinds, updatedBy, updatedAt);
        var normalizedSourceType = IngestionFilterValidation.NormalizeSourceType(sourceType);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var before = await ListForSourceInternalAsync(db, normalizedSourceType, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var existing = await db.IngestionFilters
                .FirstOrDefaultAsync(
                    f => f.SourceType == row.SourceType && f.EventKind == row.EventKind,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                db.IngestionFilters.Add(row.ToRow());
            }
            else
            {
                existing.Suppressed = row.Suppressed;
                existing.UpdatedAt = row.UpdatedAt;
                existing.UpdatedBy = row.UpdatedBy;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        var after = await ListForSourceInternalAsync(db, normalizedSourceType, cancellationToken).ConfigureAwait(false);
        return new IngestionFilterSaveResult(before, after);
    }

    public async ValueTask SeedDefaultsIfMissingAsync(DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        var inserted = false;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var seed in MDaemonIngestionFilterPolicy.DefaultFilters(updatedAt))
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ingestion_filters (
                    source_type,
                    event_kind,
                    suppressed,
                    updated_at,
                    updated_by
                )
                VALUES (
                    {seed.SourceType},
                    {seed.EventKind},
                    {seed.Suppressed},
                    {seed.UpdatedAt},
                    {seed.UpdatedBy}
                )
                ON CONFLICT (source_type, event_kind) DO NOTHING;
                """, cancellationToken).ConfigureAwait(false);
            inserted |= count > 0;
        }

        if (inserted)
        {
            await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var current = CurrentChangeVersion;
        if (current != lastSeenVersion)
        {
            return current;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var notified = false;
            connection.Notification += (_, _) => notified = true;
            await using (var listen = connection.CreateCommand())
            {
                listen.CommandText = $"LISTEN {NotifyChannel};";
                await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await connection.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return notified ? Interlocked.Increment(ref _changeVersion) : CurrentChangeVersion;
        }
        catch (NpgsqlException ex)
        {
            logger?.LogWarning(ex, "Falling back to polling for ingestion filter refresh.");
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            return CurrentChangeVersion;
        }
    }

    private static async ValueTask<IReadOnlyList<IngestionFilter>> ListForSourceInternalAsync(
        ViegardDbContext db,
        string sourceType,
        CancellationToken cancellationToken) =>
        (await db.IngestionFilters.AsNoTracking()
            .Where(f => f.SourceType == sourceType)
            .OrderBy(f => f.EventKind)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .Select(row => row.ToDomain())
        .ToList();

    private async ValueTask NotifyChangedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_notify('{NotifyChannel}', '');";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _changeVersion);
    }
}
