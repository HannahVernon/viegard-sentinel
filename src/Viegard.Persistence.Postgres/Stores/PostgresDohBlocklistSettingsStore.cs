using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Doh;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresDohBlocklistSettingsStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresDohBlocklistSettingsStore>? logger = null)
    : IDohBlocklistSettingsStore
{
    public const string NotifyChannel = "viegard_config_doh_blocklist_settings";
    private const string UniqueViolation = "23505";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<DohBlocklistSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DohBlocklistSettingsCreateResult> TryCreateAsync(
        DohBlocklistSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!DohBlocklistSettingsValidator.TryValidate(settings with { Id = DohBlocklistSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = DohBlocklistSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy);
        var utcUpdatedAt = settings.UpdatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO doh_blocklist_settings (
                id,
                enabled,
                primary_feed_url,
                secondary_feed_url,
                address_list_name,
                fetch_interval_seconds,
                probe_enabled,
                probe_canary_fqdn,
                probe_expected_token,
                probe_endpoint_path,
                probe_timeout_seconds,
                probe_concurrency,
                probe_interval_seconds,
                apply_to_routers,
                row_version,
                updated_at,
                updated_by
            )
            VALUES (
                {DohBlocklistSettings.FixedId},
                {settings.Enabled},
                {settings.PrimaryFeedUrl},
                {settings.SecondaryFeedUrl},
                {settings.AddressListName},
                {settings.FetchIntervalSeconds},
                {settings.ProbeEnabled},
                {settings.ProbeCanaryFqdn},
                {settings.ProbeExpectedToken},
                {settings.ProbeEndpointPath},
                {settings.ProbeTimeoutSeconds},
                {settings.ProbeConcurrency},
                {settings.ProbeIntervalSeconds},
                {settings.ApplyToRouters},
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
            ?? throw new InvalidOperationException("DoH blocklist settings were not created.");
        return new DohBlocklistSettingsCreateResult(inserted > 0, current);
    }

    public async ValueTask<DohBlocklistSettingsSaveResult> UpdateAsync(
        DohBlocklistSettings settings,
        int expectedRowVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        if (!DohBlocklistSettingsValidator.TryValidate(settings with { Id = DohBlocklistSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = DohBlocklistSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRowVersion == 0)
        {
            var inserted = settings with
            {
                Id = DohBlocklistSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.DohBlocklistSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return DohBlocklistSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return DohBlocklistSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.DohBlocklistSettings
            .Where(r => r.Id == DohBlocklistSettings.FixedId && r.RowVersion == expectedRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Enabled, settings.Enabled)
                .SetProperty(r => r.PrimaryFeedUrl, settings.PrimaryFeedUrl)
                .SetProperty(r => r.SecondaryFeedUrl, settings.SecondaryFeedUrl)
                .SetProperty(r => r.AddressListName, settings.AddressListName)
                .SetProperty(r => r.FetchIntervalSeconds, settings.FetchIntervalSeconds)
                .SetProperty(r => r.ProbeEnabled, settings.ProbeEnabled)
                .SetProperty(r => r.ProbeCanaryFqdn, settings.ProbeCanaryFqdn)
                .SetProperty(r => r.ProbeExpectedToken, settings.ProbeExpectedToken)
                .SetProperty(r => r.ProbeEndpointPath, settings.ProbeEndpointPath)
                .SetProperty(r => r.ProbeTimeoutSeconds, settings.ProbeTimeoutSeconds)
                .SetProperty(r => r.ProbeConcurrency, settings.ProbeConcurrency)
                .SetProperty(r => r.ProbeIntervalSeconds, settings.ProbeIntervalSeconds)
                .SetProperty(r => r.ApplyToRouters, settings.ApplyToRouters)
                .SetProperty(r => r.RowVersion, expectedRowVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return DohBlocklistSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return DohBlocklistSettingsSaveResult.Saved(
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

    private static async ValueTask<DohBlocklistSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = (await db.DohBlocklistSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == DohBlocklistSettings.FixedId, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
        if (settings is not null
            && !DohBlocklistSettingsValidator.TryValidate(settings, out var error))
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
