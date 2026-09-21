using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryLocalModelAdvisorSettingsStore : ILocalModelAdvisorSettingsStore
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private LocalModelAdvisorSettings? _settings;
    private long _changeVersion;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<LocalModelAdvisorSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask<LocalModelAdvisorSettingsCreateResult> TryCreateAsync(
        LocalModelAdvisorSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!LocalModelAdvisorSettingsValidator.TryValidate(settings with { Id = LocalModelAdvisorSettings.FixedId }, out var error))
        {
            throw new InvalidOperationException(error);
        }

        lock (_sync)
        {
            if (_settings is not null)
            {
                return ValueTask.FromResult(new LocalModelAdvisorSettingsCreateResult(false, _settings));
            }

            _settings = Normalize(settings) with
            {
                Id = LocalModelAdvisorSettings.FixedId,
                Version = 1,
                UpdatedAt = settings.UpdatedAt.ToUniversalTime(),
                UpdatedBy = LocalModelAdvisorSettingsValidator.NormalizeUpdatedBy(settings.UpdatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(new LocalModelAdvisorSettingsCreateResult(true, _settings));
        }
    }

    public ValueTask<LocalModelAdvisorSettingsSaveResult> UpsertAsync(
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

        lock (_sync)
        {
            if (_settings is null)
            {
                if (expectedVersion != 0)
                {
                    return ValueTask.FromResult(LocalModelAdvisorSettingsSaveResult.Conflict(null));
                }

                _settings = Normalize(settings) with
                {
                    Id = LocalModelAdvisorSettings.FixedId,
                    Version = 1,
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    UpdatedBy = LocalModelAdvisorSettingsValidator.NormalizeUpdatedBy(updatedBy),
                };
                SignalChanged();
                return ValueTask.FromResult(LocalModelAdvisorSettingsSaveResult.Saved(_settings));
            }

            if (_settings.Version != expectedVersion)
            {
                return ValueTask.FromResult(LocalModelAdvisorSettingsSaveResult.Conflict(_settings));
            }

            var normalized = Normalize(settings);
            _settings = _settings with
            {
                Enabled = normalized.Enabled,
                Endpoint = normalized.Endpoint,
                Model = normalized.Model,
                Temperature = normalized.Temperature,
                TimeoutMs = normalized.TimeoutMs,
                KeepAlive = normalized.KeepAlive,
                InvokeConfidenceMin = normalized.InvokeConfidenceMin,
                InvokeConfidenceMax = normalized.InvokeConfidenceMax,
                MaxSeverityDelta = normalized.MaxSeverityDelta,
                MaxConfidenceDelta = normalized.MaxConfidenceDelta,
                ResponseCacheEnabled = normalized.ResponseCacheEnabled,
                ResponseCacheTtlHours = normalized.ResponseCacheTtlHours,
                Version = _settings.Version + 1,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = LocalModelAdvisorSettingsValidator.NormalizeUpdatedBy(updatedBy),
            };
            SignalChanged();
            return ValueTask.FromResult(LocalModelAdvisorSettingsSaveResult.Saved(_settings));
        }
    }

    public ValueTask<LocalModelAdvisorSettings?> SeedIfMissingAsync(
        LocalModelAdvisorOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_settings is null)
            {
                _settings = Normalize(LocalModelAdvisorSettings.FromOptions(options, seededAt));
                SignalChanged();
            }

            return ValueTask.FromResult<LocalModelAdvisorSettings?>(_settings);
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
        return settings with
        {
            Endpoint = endpoint,
            Model = model,
            KeepAlive = keepAlive,
        };
    }
}
