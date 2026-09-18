using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Policy;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresPolicyThresholdSettingsStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresPolicyThresholdSettingsStore>? logger = null)
    : IPolicyThresholdSettingsStore
{
    public const string NotifyChannel = "viegard_config_policy_thresholds";
    private const string UniqueViolation = "23505";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<PolicyThresholdSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PolicyThresholdSettingsCreateResult> TryCreateAsync(
        PolicyThresholdSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!PolicyThresholdSettingsValidator.TryValidate(settings with { Id = PolicyThresholdSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = PolicyThresholdSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy);
        var utcUpdatedAt = settings.UpdatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO policy_threshold_settings (
                id,
                review_confidence,
                action_confidence,
                action_min_severity,
                row_version,
                updated_at,
                updated_by
            )
            VALUES (
                {PolicyThresholdSettings.FixedId},
                {settings.ReviewConfidence},
                {settings.ActionConfidence},
                {settings.ActionMinSeverity},
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
            ?? throw new InvalidOperationException("Policy threshold settings were not created.");
        return new PolicyThresholdSettingsCreateResult(inserted > 0, current);
    }

    public async ValueTask<PolicyThresholdSettingsSaveResult> UpdateAsync(
        PolicyThresholdSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        if (!PolicyThresholdSettingsValidator.TryValidate(settings with { Id = PolicyThresholdSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = PolicyThresholdSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRowVersion == 0)
        {
            var inserted = settings with
            {
                Id = PolicyThresholdSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.PolicyThresholdSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return PolicyThresholdSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return PolicyThresholdSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.PolicyThresholdSettings
            .Where(r => r.Id == PolicyThresholdSettings.FixedId && r.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.ReviewConfidence, settings.ReviewConfidence)
                .SetProperty(r => r.ActionConfidence, settings.ActionConfidence)
                .SetProperty(r => r.ActionMinSeverity, settings.ActionMinSeverity)
                .SetProperty(r => r.RowVersion, expectedRowVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return PolicyThresholdSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return PolicyThresholdSettingsSaveResult.Saved(
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

    private static async ValueTask<PolicyThresholdSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = (await db.PolicyThresholdSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == PolicyThresholdSettings.FixedId, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
        if (settings is not null
            && !PolicyThresholdSettingsValidator.TryValidate(settings, out var error))
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
