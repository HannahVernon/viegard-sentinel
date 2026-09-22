using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Classifiers;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresClassifierSettingsStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresClassifierSettingsStore>? logger = null)
    : IClassifierSettingsStore
{
    public const string NotifyChannel = "viegard_config_classifier_settings";
    private const string UniqueViolation = "23505";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<ClassifierSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ClassifierSettingsCreateResult> TryCreateAsync(
        ClassifierSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ClassifierSettingsValidator.TryValidate(settings with { Id = ClassifierSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = ClassifierSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy);
        var utcUpdatedAt = settings.UpdatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO classifier_settings (
                id,
                score_for_full_confidence,
                severity_per_score_point,
                block_recommendation_score,
                repeat_confidence_min_events,
                repeat_confidence_coefficient,
                repeat_confidence_bonus_cap,
                row_version,
                updated_at,
                updated_by
            )
            VALUES (
                {ClassifierSettings.FixedId},
                {settings.ScoreForFullConfidence},
                {settings.SeverityPerScorePoint},
                {settings.BlockRecommendationScore},
                {settings.RepeatConfidenceMinEvents},
                {settings.RepeatConfidenceCoefficient},
                {settings.RepeatConfidenceBonusCap},
                1,
                {utcUpdatedAt},
                {normalizedUpdatedBy}
            )
            ON CONFLICT (id) DO NOTHING;
            """, cancellationToken).ConfigureAwait(false);

        if (inserted > 0)
        {
            await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await GetInternalAsync(db, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Classifier settings were not created.");
        return new ClassifierSettingsCreateResult(inserted > 0, current);
    }

    public async ValueTask<ClassifierSettingsSaveResult> UpdateAsync(
        ClassifierSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        if (!ClassifierSettingsValidator.TryValidate(settings with { Id = ClassifierSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = ClassifierSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRowVersion == 0)
        {
            var inserted = settings with
            {
                Id = ClassifierSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.ClassifierSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return ClassifierSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return ClassifierSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.ClassifierSettings
            .Where(r => r.Id == ClassifierSettings.FixedId && r.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.ScoreForFullConfidence, settings.ScoreForFullConfidence)
                .SetProperty(r => r.SeverityPerScorePoint, settings.SeverityPerScorePoint)
                .SetProperty(r => r.BlockRecommendationScore, settings.BlockRecommendationScore)
                .SetProperty(r => r.RepeatConfidenceMinEvents, settings.RepeatConfidenceMinEvents)
                .SetProperty(r => r.RepeatConfidenceCoefficient, settings.RepeatConfidenceCoefficient)
                .SetProperty(r => r.RepeatConfidenceBonusCap, settings.RepeatConfidenceBonusCap)
                .SetProperty(r => r.RowVersion, expectedRowVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return ClassifierSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return ClassifierSettingsSaveResult.Saved(
            (await GetInternalAsync(db, cancellationToken).ConfigureAwait(false))!);
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

        var notified = await PostgresNotifyWait
            .WaitAsync(dataSource, NotifyChannel, timeout, logger, cancellationToken)
            .ConfigureAwait(false);
        return notified ? Interlocked.Increment(ref _changeVersion) : CurrentChangeVersion;
    }

    private static async ValueTask<ClassifierSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = (await db.ClassifierSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == ClassifierSettings.FixedId, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
        if (settings is not null
            && !ClassifierSettingsValidator.TryValidate(settings, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return settings;
    }

    private async ValueTask NotifyChangedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_notify('{NotifyChannel}', '');";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _changeVersion);
    }
}
