using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresJetPackFeedSettingsStore(IDbContextFactory<ViegardDbContext> factory) : IJetPackFeedSettingsStore
{
    private const string UniqueViolation = "23505";

    public async ValueTask<JetPackFeedSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JetPackFeedSettingsSaveResult> UpsertAsync(
        JetPackFeedSettings settings,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!JetPackFeedSettingsValidator.TryValidate(settings with { Id = JetPackFeedSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedUpdatedBy = JetPackFeedSettingsValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();
        var normalizedFeedUrl = NormalizeFeedUrl(settings.FeedUrl);
        var normalizedAddressListName = NormalizeAddressListName(settings.AddressListName);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (expectedVersion == 0)
        {
            var inserted = settings with
            {
                Id = JetPackFeedSettings.FixedId,
                FeedUrl = normalizedFeedUrl,
                AddressListName = normalizedAddressListName,
                Version = 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            db.JetPackFeedSettings.Add(inserted.ToRow());
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return JetPackFeedSettingsSaveResult.Saved(inserted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
                return JetPackFeedSettingsSaveResult.Conflict(
                    await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
            }
        }

        var updated = await db.JetPackFeedSettings
            .Where(r => r.Id == JetPackFeedSettings.FixedId && r.Version == expectedVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.FeedUrl, normalizedFeedUrl)
                .SetProperty(r => r.FetchInterval, settings.FetchInterval)
                .SetProperty(r => r.Enabled, settings.Enabled)
                .SetProperty(r => r.AddressListName, normalizedAddressListName)
                .SetProperty(r => r.Version, expectedVersion + 1)
                .SetProperty(r => r.UpdatedAt, utcUpdatedAt)
                .SetProperty(r => r.UpdatedBy, normalizedUpdatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
        {
            return JetPackFeedSettingsSaveResult.Conflict(
                await GetInternalAsync(db, cancellationToken).ConfigureAwait(false));
        }

        return JetPackFeedSettingsSaveResult.Saved(
            (await GetInternalAsync(db, cancellationToken).ConfigureAwait(false))!);
    }

    public async ValueTask<JetPackFeedSettings?> SeedIfMissingAsync(
        JetPackFeedOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var seed = JetPackFeedSettings.FromOptions(options, seededAt) with
        {
            FeedUrl = NormalizeFeedUrl(options.FeedUrl),
            AddressListName = NormalizeAddressListName(options.AddressListName),
        };
        if (!JetPackFeedSettingsValidator.TryValidate(seed, out var error))
        {
            throw new InvalidOperationException(error);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO jetpack_feed_settings (
                id,
                feed_url,
                fetch_interval,
                enabled,
                address_list_name,
                version,
                seeded_at,
                updated_at,
                updated_by
            )
            VALUES (
                {JetPackFeedSettings.FixedId},
                {seed.FeedUrl},
                {seed.FetchInterval},
                {seed.Enabled},
                {seed.AddressListName},
                {seed.Version},
                {seed.SeededAt},
                {seed.UpdatedAt},
                {seed.UpdatedBy}
            )
            ON CONFLICT (id) DO NOTHING;
            """, cancellationToken).ConfigureAwait(false);

        return await GetInternalAsync(db, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<JetPackFeedSettings?> GetInternalAsync(
        ViegardDbContext db,
        CancellationToken cancellationToken) =>
        (await db.JetPackFeedSettings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == JetPackFeedSettings.FixedId, cancellationToken)
            .ConfigureAwait(false))
        ?.ToDomain();

    private static string NormalizeFeedUrl(string value)
    {
        _ = JetPackFeedSettingsValidator.TryNormalizeFeedUrl(value, out var normalized, out var error)
            ? true
            : throw new InvalidOperationException(error);
        return normalized;
    }

    private static string NormalizeAddressListName(string value)
    {
        _ = JetPackFeedSettingsValidator.TryNormalizeAddressListName(value, out var normalized, out var error)
            ? true
            : throw new InvalidOperationException(error);
        return normalized;
    }
}
