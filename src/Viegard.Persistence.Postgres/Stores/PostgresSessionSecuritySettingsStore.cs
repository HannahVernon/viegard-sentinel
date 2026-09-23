using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Auth;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresSessionSecuritySettingsStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresSessionSecuritySettingsStore>? logger = null)
    : ISessionSecuritySettingsStore
{
    public const string NotifyChannel = "viegard_config_session_security_settings";
    private const string UniqueViolation = "23505";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<SessionSecuritySettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SessionSecuritySettingsCreateResult> TryCreateAsync(
        SessionSecuritySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!SessionSecuritySettingsValidator.TryValidate(settings with { Id = SessionSecuritySettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = SessionSecuritySettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy);
        var utcUpdatedAt = settings.UpdatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO session_security_settings (
                id,
                step_up_validity_seconds,
                resume_stash_ttl_seconds,
                row_version,
                updated_at,
                updated_by
            )
            VALUES (
                {SessionSecuritySettings.FixedId},
                {settings.StepUpValiditySeconds},
                {settings.ResumeStashTtlSeconds},
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
            ?? throw new InvalidOperationException("Session-security settings were not created.");
        return new SessionSecuritySettingsCreateResult(inserted > 0, current);
    }

    public async ValueTask<SessionSecuritySettingsSaveResult> UpdateAsync(
        SessionSecuritySettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        if (!SessionSecuritySettingsValidator.TryValidate(settings with { Id = SessionSecuritySettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = SessionSecuritySettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRowVersion == 0)
        {
            var inserted = settings with
            {
                Id = SessionSecuritySettings.FixedId,
                RowVersion = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.SessionSecuritySettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return SessionSecuritySettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return SessionSecuritySettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.SessionSecuritySettings
            .Where(r => r.Id == SessionSecuritySettings.FixedId && r.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.StepUpValiditySeconds, settings.StepUpValiditySeconds)
                .SetProperty(r => r.ResumeStashTtlSeconds, settings.ResumeStashTtlSeconds)
                .SetProperty(r => r.RowVersion, expectedRowVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return SessionSecuritySettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return SessionSecuritySettingsSaveResult.Saved(
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

    private static async ValueTask<SessionSecuritySettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = (await db.SessionSecuritySettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == SessionSecuritySettings.FixedId, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
        if (settings is not null
            && !SessionSecuritySettingsValidator.TryValidate(settings, out var error))
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
