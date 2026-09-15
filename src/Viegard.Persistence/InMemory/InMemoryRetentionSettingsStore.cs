using Viegard.Application.Retention;

namespace Viegard.Persistence.InMemory;

/// <summary>Development-only retention settings store.  Settings are process-lifetime only.</summary>
public sealed class InMemoryRetentionSettingsStore : IRetentionSettingsStore
{
    private readonly object _sync = new();
    private RetentionSettings? _settings;

    public ValueTask<RetentionSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<RetentionSettingsSaveResult> UpsertAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedVersion != 0)
                {
                    return ValueTask.FromResult(RetentionSettingsSaveResult.Conflict(null));
                }

                _settings = settings with
                {
                    Id = RetentionSettings.FixedId,
                    Version = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = RetentionSettingsValidator.NormalizeUpdatedBy(updatedBy),
                    LastCycleAt = null,
                    LastCycleCountsJson = null,
                };
                return ValueTask.FromResult(RetentionSettingsSaveResult.Saved(_settings));
            }

            if (_settings.Version != expectedVersion)
            {
                return ValueTask.FromResult(RetentionSettingsSaveResult.Conflict(_settings));
            }

            _settings = _settings with
            {
                RawObservationsDays = settings.RawObservationsDays,
                EventsDays = settings.EventsDays,
                IncidentsDays = settings.IncidentsDays,
                ClassificationsDays = settings.ClassificationsDays,
                DecisionsDays = settings.DecisionsDays,
                ActionsDays = settings.ActionsDays,
                AuditRecordsDays = settings.AuditRecordsDays,
                DeadLetteredQueueMessagesDays = settings.DeadLetteredQueueMessagesDays,
                ExpiredAdminSessionsDays = settings.ExpiredAdminSessionsDays,
                Version = _settings.Version + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = RetentionSettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            return ValueTask.FromResult(RetentionSettingsSaveResult.Saved(_settings));
        }
    }

    public ValueTask<RetentionSettings?> SeedIfMissingAsync(
        RetentionOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _settings ??= RetentionSettings.FromOptions(options, seededAt);
            return ValueTask.FromResult<RetentionSettings?>(_settings);
        }
    }

    public ValueTask UpdateLastCycleAsync(
        DateTimeOffset lastCycleAt,
        IReadOnlyDictionary<RetentionTarget, long> counts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        lock (_sync)
        {
            if (_settings is not null)
            {
                _settings = _settings with
                {
                    LastCycleAt = lastCycleAt.ToUniversalTime(),
                    LastCycleCountsJson = RetentionSettings.SerializeCounts(counts),
                };
            }
        }

        return ValueTask.CompletedTask;
    }
}
