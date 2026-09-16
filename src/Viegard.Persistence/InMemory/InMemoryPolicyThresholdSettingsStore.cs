using Viegard.Application.Policy;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryPolicyThresholdSettingsStore : IPolicyThresholdSettingsStore
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private PolicyThresholdSettings? _settings;
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<PolicyThresholdSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<PolicyThresholdSettingsCreateResult> TryCreateAsync(
        PolicyThresholdSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!PolicyThresholdSettingsValidator.TryValidate(settings with { Id = PolicyThresholdSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        lock (_sync)
        {
            if (_settings is not null)
            {
                return ValueTask.FromResult(new PolicyThresholdSettingsCreateResult(false, _settings));
            }

            _settings = settings with
            {
                Id = PolicyThresholdSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = settings.UpdatedAt.ToUniversalTime(),
                UpdatedBy = PolicyThresholdSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(new PolicyThresholdSettingsCreateResult(true, _settings));
        }
    }

    public ValueTask<PolicyThresholdSettingsSaveResult> UpdateAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedRowVersion != 0)
                {
                    return ValueTask.FromResult(PolicyThresholdSettingsSaveResult.Conflict(null));
                }

                _settings = settings with
                {
                    Id = PolicyThresholdSettings.FixedId,
                    RowVersion = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = PolicyThresholdSettingsValidator.NormalizeUpdatedBy(updatedBy),
                };
                SignalChanged();
                return ValueTask.FromResult(PolicyThresholdSettingsSaveResult.Saved(_settings));
            }

            if (_settings.RowVersion != expectedRowVersion)
            {
                return ValueTask.FromResult(PolicyThresholdSettingsSaveResult.Conflict(_settings));
            }

            _settings = _settings with
            {
                ReviewConfidence = settings.ReviewConfidence,
                ActionConfidence = settings.ActionConfidence,
                ActionMinSeverity = settings.ActionMinSeverity,
                RowVersion = _settings.RowVersion + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = PolicyThresholdSettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(PolicyThresholdSettingsSaveResult.Saved(_settings));
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
