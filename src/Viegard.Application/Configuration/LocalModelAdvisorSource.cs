namespace Viegard.Application.Configuration;

public interface ILocalModelAdvisorDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullLocalModelAdvisorDiagnostics : ILocalModelAdvisorDiagnostics
{
    public static NullLocalModelAdvisorDiagnostics Instance { get; } = new();

    private NullLocalModelAdvisorDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

/// <summary>
/// Last-known-good snapshot of the database-owned local-model advisor settings.
/// Falls back to disabled bootstrap options until the settings row is seeded.
/// </summary>
public sealed class LocalModelAdvisorSource(
    ILocalModelAdvisorSettingsStore? settingsStore = null,
    ILocalModelAdvisorDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly ILocalModelAdvisorDiagnostics _diagnostics = diagnostics ?? NullLocalModelAdvisorDiagnostics.Instance;
    private LocalModelAdvisorSnapshot _snapshot = LocalModelAdvisorSnapshot.Unseeded;

    public LocalModelAdvisorSnapshot Current => Volatile.Read(ref _snapshot);

    public LocalModelAdvisorValues CurrentValues(LocalModelAdvisorOptions fallbackOptions) =>
        Current.ValuesOrFallback(fallbackOptions);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (settingsStore is null)
        {
            return;
        }

        try
        {
            var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(
                ref _snapshot,
                settings is null
                    ? LocalModelAdvisorSnapshot.Unseeded
                    : LocalModelAdvisorSnapshot.Seeded(settings));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.RefreshFailed(ex);
        }
    }

    public async Task RunRefreshLoopAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (settingsStore is null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        var seenVersion = settingsStore.CurrentChangeVersion;
        while (!cancellationToken.IsCancellationRequested)
        {
            seenVersion = await settingsStore
                .WaitForChangeAsync(seenVersion, RefreshInterval, cancellationToken)
                .ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record LocalModelAdvisorValues(
    bool Enabled,
    string Endpoint,
    string Model,
    double Temperature,
    int TimeoutMs,
    string KeepAlive,
    double InvokeConfidenceMin,
    double InvokeConfidenceMax,
    int MaxSeverityDelta,
    double MaxConfidenceDelta)
{
    public static LocalModelAdvisorValues FromOptions(LocalModelAdvisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeEndpoint(options.Endpoint, out var endpoint, out _);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeModel(options.Model, out var model, out _);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeKeepAlive(options.KeepAlive, out var keepAlive, out _);
        return new LocalModelAdvisorValues(
            options.Enabled,
            endpoint,
            model,
            options.Temperature,
            options.TimeoutMs,
            keepAlive,
            options.InvokeConfidenceMin,
            options.InvokeConfidenceMax,
            options.MaxSeverityDelta,
            options.MaxConfidenceDelta);
    }

    public static LocalModelAdvisorValues FromSettings(LocalModelAdvisorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeEndpoint(settings.Endpoint, out var endpoint, out _);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeModel(settings.Model, out var model, out _);
        _ = LocalModelAdvisorSettingsValidator.TryNormalizeKeepAlive(settings.KeepAlive, out var keepAlive, out _);
        return new LocalModelAdvisorValues(
            settings.Enabled,
            endpoint,
            model,
            settings.Temperature,
            settings.TimeoutMs,
            keepAlive,
            settings.InvokeConfidenceMin,
            settings.InvokeConfidenceMax,
            settings.MaxSeverityDelta,
            settings.MaxConfidenceDelta);
    }
}

public sealed record LocalModelAdvisorSnapshot(
    bool IsSeeded,
    bool Enabled,
    string Endpoint,
    string Model,
    double Temperature,
    int TimeoutMs,
    string KeepAlive,
    double InvokeConfidenceMin,
    double InvokeConfidenceMax,
    int MaxSeverityDelta,
    double MaxConfidenceDelta,
    int Version)
{
    public static LocalModelAdvisorSnapshot Unseeded { get; } = new(
        false,
        false,
        LocalModelAdvisorSettings.DefaultEndpoint,
        LocalModelAdvisorSettings.DefaultModel,
        0.0,
        8000,
        LocalModelAdvisorSettings.DefaultKeepAlive,
        0.50,
        0.85,
        3,
        0.20,
        0);

    public static LocalModelAdvisorSnapshot Seeded(LocalModelAdvisorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = LocalModelAdvisorValues.FromSettings(settings);
        return new LocalModelAdvisorSnapshot(
            true,
            values.Enabled,
            values.Endpoint,
            values.Model,
            values.Temperature,
            values.TimeoutMs,
            values.KeepAlive,
            values.InvokeConfidenceMin,
            values.InvokeConfidenceMax,
            values.MaxSeverityDelta,
            values.MaxConfidenceDelta,
            settings.Version);
    }

    public LocalModelAdvisorValues ValuesOrFallback(LocalModelAdvisorOptions fallbackOptions) =>
        IsSeeded
            ? new LocalModelAdvisorValues(
                Enabled,
                Endpoint,
                Model,
                Temperature,
                TimeoutMs,
                KeepAlive,
                InvokeConfidenceMin,
                InvokeConfidenceMax,
                MaxSeverityDelta,
                MaxConfidenceDelta)
            : LocalModelAdvisorValues.FromOptions(fallbackOptions);
}
