using Viegard.Application.Doh;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryDohBlocklistSettingsStore : IDohBlocklistSettingsStore
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private DohBlocklistSettings? _settings;
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<DohBlocklistSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<DohBlocklistSettingsCreateResult> TryCreateAsync(
        DohBlocklistSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!DohBlocklistSettingsValidator.TryValidate(settings with { Id = DohBlocklistSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        lock (_sync)
        {
            if (_settings is not null)
            {
                return ValueTask.FromResult(new DohBlocklistSettingsCreateResult(false, _settings));
            }

            _settings = settings with
            {
                Id = DohBlocklistSettings.FixedId,
                RowVersion = 1,
                UpdatedAt = settings.UpdatedAt.ToUniversalTime(),
                UpdatedBy = DohBlocklistSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(new DohBlocklistSettingsCreateResult(true, _settings));
        }
    }

    public ValueTask<DohBlocklistSettingsSaveResult> UpdateAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedRowVersion != 0)
                {
                    return ValueTask.FromResult(DohBlocklistSettingsSaveResult.Conflict(null));
                }

                _settings = settings with
                {
                    Id = DohBlocklistSettings.FixedId,
                    RowVersion = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = DohBlocklistSettingsValidator.NormalizeUpdatedBy(updatedBy),
                };
                SignalChanged();
                return ValueTask.FromResult(DohBlocklistSettingsSaveResult.Saved(_settings));
            }

            if (_settings.RowVersion != expectedRowVersion)
            {
                return ValueTask.FromResult(DohBlocklistSettingsSaveResult.Conflict(_settings));
            }

            _settings = settings with
            {
                Id = DohBlocklistSettings.FixedId,
                RowVersion = _settings.RowVersion + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = DohBlocklistSettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(DohBlocklistSettingsSaveResult.Saved(_settings));
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
