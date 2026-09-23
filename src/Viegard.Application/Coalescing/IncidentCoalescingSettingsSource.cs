namespace Viegard.Application.Coalescing;

public interface IIncidentCoalescingSettingsDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullIncidentCoalescingSettingsDiagnostics : IIncidentCoalescingSettingsDiagnostics
{
    public static NullIncidentCoalescingSettingsDiagnostics Instance { get; } = new();

    private NullIncidentCoalescingSettingsDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

/// <summary>
/// Holds the current incident-coalescing settings snapshot and refreshes it when
/// the durable row changes.  Mirrors <c>BurstDetectionSettingsSource</c>.
/// </summary>
public sealed class IncidentCoalescingSettingsSource(
    IIncidentCoalescingSettingsStore? settingsStore = null,
    IIncidentCoalescingSettingsDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly IIncidentCoalescingSettingsDiagnostics _diagnostics = diagnostics ?? NullIncidentCoalescingSettingsDiagnostics.Instance;
    private IncidentCoalescingSettingsSnapshot _snapshot = IncidentCoalescingSettingsSnapshot.Unseeded;

    public IncidentCoalescingSettingsSnapshot Current => Volatile.Read(ref _snapshot);

    public IncidentCoalescingValues CurrentValues(IncidentCoalescingOptions fallbackOptions) =>
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
                    ? IncidentCoalescingSettingsSnapshot.Unseeded
                    : IncidentCoalescingSettingsSnapshot.Seeded(settings));
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

public sealed record IncidentCoalescingSettingsSnapshot(
    bool IsSeeded,
    bool Enabled,
    int SettleWindowSeconds,
    int MaxCoalesceWindowSeconds,
    int RowVersion)
{
    public static IncidentCoalescingSettingsSnapshot Unseeded { get; } = new(
        false,
        false,
        0,
        0,
        0);

    public static IncidentCoalescingSettingsSnapshot Seeded(IncidentCoalescingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new IncidentCoalescingSettingsSnapshot(
            true,
            settings.Enabled,
            settings.SettleWindowSeconds,
            settings.MaxCoalesceWindowSeconds,
            settings.RowVersion);
    }

    public IncidentCoalescingValues ValuesOrFallback(IncidentCoalescingOptions fallbackOptions) =>
        IsSeeded
            ? new IncidentCoalescingValues(
                Enabled,
                SettleWindowSeconds,
                MaxCoalesceWindowSeconds)
            : IncidentCoalescingValues.FromOptions(fallbackOptions);
}
