using Viegard.Application.Burst;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryBurstDetectionSettingsStore : IBurstDetectionSettingsStore
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private BurstDetectionSettings? _settings;
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<BurstDetectionSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<BurstDetectionSettingsCreateResult> TryCreateAsync(
        BurstDetectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!BurstDetectionSettingsValidator.TryValidate(settings with { Id = BurstDetectionSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        lock (_sync)
        {
            if (_settings is not null)
            {
                return ValueTask.FromResult(new BurstDetectionSettingsCreateResult(false, _settings));
            }

            _settings = settings with
            {
                Id = BurstDetectionSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = settings.UpdatedAt.ToUniversalTime(),
                UpdatedBy = BurstDetectionSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(new BurstDetectionSettingsCreateResult(true, _settings));
        }
    }

    public ValueTask<BurstDetectionSettingsSaveResult> UpdateAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedRowVersion != 0)
                {
                    return ValueTask.FromResult(BurstDetectionSettingsSaveResult.Conflict(null));
                }

                _settings = settings with
                {
                    Id = BurstDetectionSettings.FixedId,
                    RowVersion = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = BurstDetectionSettingsValidator.NormalizeUpdatedBy(updatedBy),
                };
                SignalChanged();
                return ValueTask.FromResult(BurstDetectionSettingsSaveResult.Saved(_settings));
            }

            if (_settings.RowVersion != expectedRowVersion)
            {
                return ValueTask.FromResult(BurstDetectionSettingsSaveResult.Conflict(_settings));
            }

            _settings = _settings with
            {
                GlobalEnabled = settings.GlobalEnabled,
                AuthFailureEnabled = settings.AuthFailureEnabled,
                AuthFailureThreshold = settings.AuthFailureThreshold,
                AuthFailureWindowSeconds = settings.AuthFailureWindowSeconds,
                AuthFailureCooldownSeconds = settings.AuthFailureCooldownSeconds,
                AuthFailureActionEligible = settings.AuthFailureActionEligible,
                RowVersion = _settings.RowVersion + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = BurstDetectionSettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(BurstDetectionSettingsSaveResult.Saved(_settings));
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
