using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Burst;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresBurstDetectionSettingsStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresBurstDetectionSettingsStore>? logger = null)
    : IBurstDetectionSettingsStore
{
    public const string NotifyChannel = "viegard_config_burst_detection_settings";
    private const string UniqueViolation = "23505";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<BurstDetectionSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BurstDetectionSettingsCreateResult> TryCreateAsync(
        BurstDetectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!BurstDetectionSettingsValidator.TryValidate(settings with { Id = BurstDetectionSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = BurstDetectionSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy);
        var utcUpdatedAt = settings.UpdatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO burst_detection_settings (
                id,
                global_enabled,
                auth_failure_enabled,
                auth_failure_threshold,
                auth_failure_window_seconds,
                auth_failure_cooldown_seconds,
                auth_failure_action_eligible,
                row_version,
                updated_at,
                updated_by
            )
            VALUES (
                {BurstDetectionSettings.FixedId},
                {settings.GlobalEnabled},
                {settings.AuthFailureEnabled},
                {settings.AuthFailureThreshold},
                {settings.AuthFailureWindowSeconds},
                {settings.AuthFailureCooldownSeconds},
                {settings.AuthFailureActionEligible},
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
            ?? throw new InvalidOperationException("Burst-detection settings were not created.");
        return new BurstDetectionSettingsCreateResult(inserted > 0, current);
    }

    public async ValueTask<BurstDetectionSettingsSaveResult> UpdateAsync(
        BurstDetectionSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        if (!BurstDetectionSettingsValidator.TryValidate(settings with { Id = BurstDetectionSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = BurstDetectionSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRowVersion == 0)
        {
            var inserted = settings with
            {
                Id = BurstDetectionSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.BurstDetectionSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return BurstDetectionSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return BurstDetectionSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.BurstDetectionSettings
            .Where(r => r.Id == BurstDetectionSettings.FixedId && r.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.GlobalEnabled, settings.GlobalEnabled)
                .SetProperty(r => r.AuthFailureEnabled, settings.AuthFailureEnabled)
                .SetProperty(r => r.AuthFailureThreshold, settings.AuthFailureThreshold)
                .SetProperty(r => r.AuthFailureWindowSeconds, settings.AuthFailureWindowSeconds)
                .SetProperty(r => r.AuthFailureCooldownSeconds, settings.AuthFailureCooldownSeconds)
                .SetProperty(r => r.AuthFailureActionEligible, settings.AuthFailureActionEligible)
                .SetProperty(r => r.RowVersion, expectedRowVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return BurstDetectionSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return BurstDetectionSettingsSaveResult.Saved(
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

    private static async ValueTask<BurstDetectionSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = (await db.BurstDetectionSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == BurstDetectionSettings.FixedId, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
        if (settings is not null
            && !BurstDetectionSettingsValidator.TryValidate(settings, out var error))
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
