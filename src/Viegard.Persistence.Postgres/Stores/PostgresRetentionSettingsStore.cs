using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viegard.Application.Retention;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresRetentionSettingsStore(IDbContextFactory<ViegardDbContext> factory) : IRetentionSettingsStore
{
    private const string UniqueViolation = "23505";

    public async ValueTask<RetentionSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RetentionSettingsSaveResult> UpsertAsync(
        RetentionSettings settings,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!RetentionSettingsValidator.TryValidate(settings with { Id = RetentionSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = RetentionSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedVersion == 0)
        {
            var inserted = settings with
            {
                Id = RetentionSettings.FixedId,
                Version = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
                LastCycleAt = null,
                LastCycleCountsJson = null,
            };
            db.RetentionSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return RetentionSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return RetentionSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.RetentionSettings
            .Where(r => r.Id == RetentionSettings.FixedId && r.Version == expectedVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.RawObservationsDays, settings.RawObservationsDays)
                .SetProperty(r => r.EventsDays, settings.EventsDays)
                .SetProperty(r => r.IncidentsDays, settings.IncidentsDays)
                .SetProperty(r => r.ClassificationsDays, settings.ClassificationsDays)
                .SetProperty(r => r.DecisionsDays, settings.DecisionsDays)
                .SetProperty(r => r.ActionsDays, settings.ActionsDays)
                .SetProperty(r => r.AuditRecordsDays, settings.AuditRecordsDays)
                .SetProperty(r => r.DeadLetteredQueueMessagesDays, settings.DeadLetteredQueueMessagesDays)
                .SetProperty(r => r.ExpiredAdminSessionsDays, settings.ExpiredAdminSessionsDays)
                .SetProperty(r => r.Version, expectedVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return RetentionSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        return RetentionSettingsSaveResult.Saved(
            (await GetInternalAsync(db, cancellationToken).ConfigureAwait(false))!);
    }

    public async ValueTask<RetentionSettings?> SeedIfMissingAsync(
        RetentionOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var seed = RetentionSettings.FromOptions(options, seededAt);
        if (!RetentionSettingsValidator.TryValidate(seed, out var error))
        {
            throw new InvalidOperationException(error);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO retention_settings (
                id,
                raw_observations_days,
                events_days,
                incidents_days,
                classifications_days,
                decisions_days,
                actions_days,
                audit_records_days,
                dead_lettered_queue_messages_days,
                expired_admin_sessions_days,
                version,
                seeded_at,
                updated_at,
                updated_by,
                last_cycle_at,
                last_cycle_counts_json
            )
            VALUES (
                {RetentionSettings.FixedId},
                {seed.RawObservationsDays},
                {seed.EventsDays},
                {seed.IncidentsDays},
                {seed.ClassificationsDays},
                {seed.DecisionsDays},
                {seed.ActionsDays},
                {seed.AuditRecordsDays},
                {seed.DeadLetteredQueueMessagesDays},
                {seed.ExpiredAdminSessionsDays},
                {seed.Version},
                {seed.SeededAt},
                {seed.UpdatedAt},
                {seed.UpdatedBy},
                NULL,
                NULL
            )
            ON CONFLICT (id) DO NOTHING;
            """, cancellationToken).ConfigureAwait(false);

        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateLastCycleAsync(
        DateTimeOffset lastCycleAt,
        IReadOnlyDictionary<RetentionTarget, long> counts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var utcLastCycleAt = lastCycleAt.ToUniversalTime();
        var countsJson = RetentionSettings.SerializeCounts(counts);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.RetentionSettings
            .Where(r => r.Id == RetentionSettings.FixedId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.LastCycleAt, utcLastCycleAt)
                .SetProperty(r => r.LastCycleCountsJson, countsJson),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<RetentionSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken) =>
        (await db.RetentionSettings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == RetentionSettings.FixedId, cancellationToken)
            .ConfigureAwait(false))
        ?.ToDomain();
}
