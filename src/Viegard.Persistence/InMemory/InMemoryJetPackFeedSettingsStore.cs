using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

/// <summary>Development-only JetPack feed settings store.  Settings are process-lifetime only.</summary>
public sealed class InMemoryJetPackFeedSettingsStore : IJetPackFeedSettingsStore
{
    private readonly object _sync = new();
    private JetPackFeedSettings? _settings;

    public ValueTask<JetPackFeedSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<JetPackFeedSettingsSaveResult> UpsertAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedVersion != 0)
                {
                    return ValueTask.FromResult(JetPackFeedSettingsSaveResult.Conflict(null));
                }

                _settings = settings with
                {
                    Id = JetPackFeedSettings.FixedId,
                    FeedUrl = NormalizeFeedUrl(settings.FeedUrl),
                    AddressListName = NormalizeAddressListName(settings.AddressListName),
                    Version = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = JetPackFeedSettingsValidator.NormalizeUpdatedBy(updatedBy),
                };
                return ValueTask.FromResult(JetPackFeedSettingsSaveResult.Saved(_settings));
            }

            if (_settings.Version != expectedVersion)
            {
                return ValueTask.FromResult(JetPackFeedSettingsSaveResult.Conflict(_settings));
            }

            _settings = _settings with
            {
                FeedUrl = NormalizeFeedUrl(settings.FeedUrl),
                FetchInterval = settings.FetchInterval,
                Enabled = settings.Enabled,
                AddressListName = NormalizeAddressListName(settings.AddressListName),
                Version = _settings.Version + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = JetPackFeedSettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            return ValueTask.FromResult(JetPackFeedSettingsSaveResult.Saved(_settings));
        }
    }

    public ValueTask<JetPackFeedSettings?> SeedIfMissingAsync(
        JetPackFeedOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _settings ??= JetPackFeedSettings.FromOptions(options, seededAt) with
            {
                FeedUrl = NormalizeFeedUrl(options.FeedUrl),
                AddressListName = NormalizeAddressListName(options.AddressListName),
            };
            return ValueTask.FromResult<JetPackFeedSettings?>(_settings);
        }
    }

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
