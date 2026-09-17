using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Policy;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresPolicyPostureSettingsStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresPolicyPostureSettingsStore>? logger = null)
    : IPolicyPostureSettingsStore
{
    public const string NotifyChannel = "viegard_config_policy_posture";
    private const string UniqueViolation = "23505";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<PolicyPostureSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PolicyPostureSettingsCreateResult> TryCreateAsync(
        PolicyPostureSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!PolicyPostureSettingsValidator.TryValidate(settings with { Id = PolicyPostureSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = PolicyPostureSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy);
        var utcUpdatedAt = settings.UpdatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO policy_posture_settings (
                id,
                dry_run,
                manual_approval_mode,
                emergency_stop,
                row_version,
                updated_at,
                updated_by
            )
            VALUES (
                {PolicyPostureSettings.FixedId},
                {settings.DryRun},
                {settings.ManualApprovalMode},
                {settings.EmergencyStop},
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
            ?? throw new InvalidOperationException("Policy posture settings were not created.");
        return new PolicyPostureSettingsCreateResult(inserted > 0, current);
    }

    public async ValueTask<PolicyPostureSettingsSaveResult> UpdateAsync(
        PolicyPostureSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        if (!PolicyPostureSettingsValidator.TryValidate(settings with { Id = PolicyPostureSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = PolicyPostureSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRowVersion == 0)
        {
            var inserted = settings with
            {
                Id = PolicyPostureSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.PolicyPostureSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return PolicyPostureSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return PolicyPostureSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.PolicyPostureSettings
            .Where(r => r.Id == PolicyPostureSettings.FixedId && r.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.DryRun, settings.DryRun)
                .SetProperty(r => r.ManualApprovalMode, settings.ManualApprovalMode)
                .SetProperty(r => r.EmergencyStop, settings.EmergencyStop)
                .SetProperty(r => r.RowVersion, expectedRowVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return PolicyPostureSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return PolicyPostureSettingsSaveResult.Saved(
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
            logger?.LogWarning(ex, "Falling back to polling for policy posture refresh.");
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            return CurrentChangeVersion;
        }
    }

    private static async ValueTask<PolicyPostureSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = (await db.PolicyPostureSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == PolicyPostureSettings.FixedId, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
        if (settings is not null
            && !PolicyPostureSettingsValidator.TryValidate(settings, out var error))
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
