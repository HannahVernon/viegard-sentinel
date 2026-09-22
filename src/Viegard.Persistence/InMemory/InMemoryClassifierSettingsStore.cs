using Viegard.Application.Classifiers;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryClassifierSettingsStore : IClassifierSettingsStore
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private ClassifierSettings? _settings;
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<ClassifierSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<ClassifierSettingsCreateResult> TryCreateAsync(
        ClassifierSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ClassifierSettingsValidator.TryValidate(settings with { Id = ClassifierSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        lock (_sync)
        {
            if (_settings is not null)
            {
                return ValueTask.FromResult(new ClassifierSettingsCreateResult(false, _settings));
            }

            _settings = settings with
            {
                Id = ClassifierSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = settings.UpdatedAt.ToUniversalTime(),
                UpdatedBy = ClassifierSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(new ClassifierSettingsCreateResult(true, _settings));
        }
    }

    public ValueTask<ClassifierSettingsSaveResult> UpdateAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedRowVersion != 0)
                {
                    return ValueTask.FromResult(ClassifierSettingsSaveResult.Conflict(null));
                }

                _settings = settings with
                {
                    Id = ClassifierSettings.FixedId,
                    RowVersion = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = ClassifierSettingsValidator.NormalizeUpdatedBy(updatedBy),
                };
                SignalChanged();
                return ValueTask.FromResult(ClassifierSettingsSaveResult.Saved(_settings));
            }

            if (_settings.RowVersion != expectedRowVersion)
            {
                return ValueTask.FromResult(ClassifierSettingsSaveResult.Conflict(_settings));
            }

            _settings = _settings with
            {
                ScoreForFullConfidence = settings.ScoreForFullConfidence,
                SeverityPerScorePoint = settings.SeverityPerScorePoint,
                BlockRecommendationScore = settings.BlockRecommendationScore,
                RepeatConfidenceMinEvents = settings.RepeatConfidenceMinEvents,
                RepeatConfidenceCoefficient = settings.RepeatConfidenceCoefficient,
                RepeatConfidenceBonusCap = settings.RepeatConfidenceBonusCap,
                RowVersion = _settings.RowVersion + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = ClassifierSettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(ClassifierSettingsSaveResult.Saved(_settings));
        }
    }

    public async ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<long> waiter;
        lock (_sync)
        {
            var current = CurrentChangeVersion;
            if (current != lastSeenVersion)
            {
                return current;
            }

            waiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = timeoutCts.Token.Register(() => waiter.TrySetResult(CurrentChangeVersion));
        timeoutCts.CancelAfter(timeout);
        var result = await waiter.Task.ConfigureAwait(false);

        lock (_sync)
        {
            _waiters.Remove(waiter);
        }

        return result;
    }

    private void SignalChanged()
    {
        var version = Interlocked.Increment(ref _changeVersion);
        foreach (var waiter in _waiters.ToArray())
        {
            waiter.TrySetResult(version);
        }
    }
}
