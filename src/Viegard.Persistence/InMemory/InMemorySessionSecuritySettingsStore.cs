using Viegard.Application.Auth;

namespace Viegard.Persistence.InMemory;

public sealed class InMemorySessionSecuritySettingsStore : ISessionSecuritySettingsStore
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private SessionSecuritySettings? _settings;
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<SessionSecuritySettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<SessionSecuritySettingsCreateResult> TryCreateAsync(
        SessionSecuritySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!SessionSecuritySettingsValidator.TryValidate(settings with { Id = SessionSecuritySettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        lock (_sync)
        {
            if (_settings is not null)
            {
                return ValueTask.FromResult(new SessionSecuritySettingsCreateResult(false, _settings));
            }

            _settings = settings with
            {
                Id = SessionSecuritySettings.FixedId,
                RowVersion = 1,
                UpdatedAt = settings.UpdatedAt.ToUniversalTime(),
                UpdatedBy = SessionSecuritySettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(new SessionSecuritySettingsCreateResult(true, _settings));
        }
    }

    public ValueTask<SessionSecuritySettingsSaveResult> UpdateAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedRowVersion != 0)
                {
                    return ValueTask.FromResult(SessionSecuritySettingsSaveResult.Conflict(null));
                }

                _settings = settings with
                {
                    Id = SessionSecuritySettings.FixedId,
                    RowVersion = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = SessionSecuritySettingsValidator.NormalizeUpdatedBy(updatedBy),
                };
                SignalChanged();
                return ValueTask.FromResult(SessionSecuritySettingsSaveResult.Saved(_settings));
            }

            if (_settings.RowVersion != expectedRowVersion)
            {
                return ValueTask.FromResult(SessionSecuritySettingsSaveResult.Conflict(_settings));
            }

            _settings = _settings with
            {
                StepUpValiditySeconds = settings.StepUpValiditySeconds,
                ResumeStashTtlSeconds = settings.ResumeStashTtlSeconds,
                RowVersion = _settings.RowVersion + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = SessionSecuritySettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(SessionSecuritySettingsSaveResult.Saved(_settings));
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
