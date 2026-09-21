using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresLocalModelAdvisorSettingsStore(
    IDbContextFactory<ViegardDbContext> factory,
    NpgsqlDataSource dataSource,
    ILogger<PostgresLocalModelAdvisorSettingsStore>? logger = null)
    : ILocalModelAdvisorSettingsStore
{
    public const string NotifyChannel = "viegard_config_local_model_advisor";
    private const string UniqueViolation = "23505";
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<LocalModelAdvisorSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LocalModelAdvisorSettingsCreateResult> TryCreateAsync(
        LocalModelAdvisorSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!LocalModelAdvisorSettingsValidator.TryValidate(settings with { Id = LocalModelAdvisorSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var seed = Normalize(settings) with
        {
            Id = LocalModelAdvisorSettings.FixedId,
            Version = 1,
            UpdatedAt = settings.UpdatedAt.ToUniversalTime(),
            UpdatedBy = LocalModelAdvisorSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy),
        };

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO local_model_advisor_settings (
                id,
                enabled,
                endpoint,
                model,
                temperature,
                timeout_ms,
                keep_alive,
                invoke_confidence_min,
                invoke_confidence_max,
                max_severity_delta,
                max_confidence_delta,
                response_cache_enabled,
                response_cache_ttl_hours,
                ensemble_enabled,
                second_model_endpoint,
                second_model,
                injection_action,
                version,
                seeded_at,
                updated_at,
                updated_by
            )
            VALUES (
                {LocalModelAdvisorSettings.FixedId},
                {seed.Enabled},
                {seed.Endpoint},
                {seed.Model},
                {seed.Temperature},
                {seed.TimeoutMs},
                {seed.KeepAlive},
                {seed.InvokeConfidenceMin},
                {seed.InvokeConfidenceMax},
                {seed.MaxSeverityDelta},
                {seed.MaxConfidenceDelta},
                {seed.ResponseCacheEnabled},
                {seed.ResponseCacheTtlHours},
                {seed.EnsembleEnabled},
                {seed.SecondModelEndpoint},
                {seed.SecondModel},
                {(int)seed.InjectionAction},
                {seed.Version},
                {seed.SeededAt},
                {seed.UpdatedAt},
                {seed.UpdatedBy}
            )
            ON CONFLICT (id) DO NOTHING;
            """, cancellationToken).ConfigureAwait(false);

        if (inserted > 0)
        {
            await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await GetInternalAsync(db, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Local-model advisor settings were not created.");
        return new LocalModelAdvisorSettingsCreateResult(inserted > 0, current);
    }

    public async ValueTask<LocalModelAdvisorSettingsSaveResult> UpsertAsync(
        LocalModelAdvisorSettings settings,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!LocalModelAdvisorSettingsValidator.TryValidate(settings with { Id = LocalModelAdvisorSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalized = Normalize(settings);
        var normalizedUpdatedBy = LocalModelAdvisorSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedVersion == 0)
        {
            var inserted = normalized with
            {
                Id = LocalModelAdvisorSettings.FixedId,
                Version = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.LocalModelAdvisorSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
                return LocalModelAdvisorSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return LocalModelAdvisorSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.LocalModelAdvisorSettings
            .Where(r => r.Id == LocalModelAdvisorSettings.FixedId && r.Version == expectedVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Enabled, normalized.Enabled)
                .SetProperty(r => r.Endpoint, normalized.Endpoint)
                .SetProperty(r => r.Model, normalized.Model)
                .SetProperty(r => r.Temperature, normalized.Temperature)
                .SetProperty(r => r.TimeoutMs, normalized.TimeoutMs)
                .SetProperty(r => r.KeepAlive, normalized.KeepAlive)
                .SetProperty(r => r.InvokeConfidenceMin, normalized.InvokeConfidenceMin)
                .SetProperty(r => r.InvokeConfidenceMax, normalized.InvokeConfidenceMax)
                .SetProperty(r => r.MaxSeverityDelta, normalized.MaxSeverityDelta)
                .SetProperty(r => r.MaxConfidenceDelta, normalized.MaxConfidenceDelta)
                .SetProperty(r => r.ResponseCacheEnabled, normalized.ResponseCacheEnabled)
                .SetProperty(r => r.ResponseCacheTtlHours, normalized.ResponseCacheTtlHours)
                .SetProperty(r => r.EnsembleEnabled, normalized.EnsembleEnabled)
                .SetProperty(r => r.SecondModelEndpoint, normalized.SecondModelEndpoint)
                .SetProperty(r => r.SecondModel, normalized.SecondModel)
                .SetProperty(r => r.InjectionAction, (int)normalized.InjectionAction)
                .SetProperty(r => r.Version, expectedVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return LocalModelAdvisorSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return LocalModelAdvisorSettingsSaveResult.Saved(
            (await GetInternalAsync(db, cancellationToken).ConfigureAwait(false))!);
    }

    public async ValueTask<LocalModelAdvisorSettings?> SeedIfMissingAsync(
        LocalModelAdvisorOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var seed = Normalize(LocalModelAdvisorSettings.FromOptions(options, seededAt));
        var result = await TryCreateAsync(seed, cancellationToken).ConfigureAwait(false);
        return result.Settings;
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

    private static async ValueTask<LocalModelAdvisorSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = (await db.LocalModelAdvisorSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == LocalModelAdvisorSettings.FixedId, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
        if (settings is not null
            && !LocalModelAdvisorSettingsValidator.TryValidate(settings, out var error))
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

    private static LocalModelAdvisorSettings Normalize(LocalModelAdvisorSettings settings)
    {
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeEndpoint(settings.Endpoint, out var endpoint, out var endpointError)
            ? true
            : throw new InvalidOperationException(endpointError);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeModel(settings.Model, out var model, out var modelError)
            ? true
            : throw new InvalidOperationException(modelError);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeKeepAlive(settings.KeepAlive, out var keepAlive, out var keepAliveError)
            ? true
            : throw new InvalidOperationException(keepAliveError);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeSecondModelEndpoint(settings.SecondModelEndpoint, settings.EnsembleEnabled, out var secondModelEndpoint, out var secondModelEndpointError)
            ? true
            : throw new InvalidOperationException(secondModelEndpointError);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeSecondModel(settings.SecondModel, settings.EnsembleEnabled, out var secondModel, out var secondModelError)
            ? true
            : throw new InvalidOperationException(secondModelError);
        return settings with
        {
            Endpoint = endpoint,
            Model = model,
            KeepAlive = keepAlive,
            SecondModelEndpoint = secondModelEndpoint,
            SecondModel = secondModel,
        };
    }
}
