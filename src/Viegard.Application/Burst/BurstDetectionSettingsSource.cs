namespace Viegard.Application.Burst;

public interface IBurstDetectionSettingsDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullBurstDetectionSettingsDiagnostics : IBurstDetectionSettingsDiagnostics
{
    public static NullBurstDetectionSettingsDiagnostics Instance { get; } = new();

    private NullBurstDetectionSettingsDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

/// <summary>
/// Holds the current burst-detection settings snapshot and refreshes it when the
/// durable row changes.  Mirrors <c>ClassifierSettingsSource</c>.
/// </summary>
public sealed class BurstDetectionSettingsSource(
    IBurstDetectionSettingsStore? settingsStore = null,
    IBurstDetectionSettingsDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly IBurstDetectionSettingsDiagnostics _diagnostics = diagnostics ?? NullBurstDetectionSettingsDiagnostics.Instance;
    private BurstDetectionSettingsSnapshot _snapshot = BurstDetectionSettingsSnapshot.Unseeded;

    public BurstDetectionSettingsSnapshot Current => Volatile.Read(ref _snapshot);

    public BurstDetectionValues CurrentValues(BurstDetectionOptions fallbackOptions) =>
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
                    ? BurstDetectionSettingsSnapshot.Unseeded
                    : BurstDetectionSettingsSnapshot.Seeded(settings));
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

public sealed record BurstDetectionSettingsSnapshot(
    bool IsSeeded,
    bool GlobalEnabled,
    bool AuthFailureEnabled,
    int AuthFailureThreshold,
    int AuthFailureWindowSeconds,
    int AuthFailureCooldownSeconds,
    bool AuthFailureActionEligible,
    int RowVersion)
{
    public static BurstDetectionSettingsSnapshot Unseeded { get; } = new(
        false,
        false,
        false,
        0,
        0,
        0,
        false,
        0);

    public static BurstDetectionSettingsSnapshot Seeded(BurstDetectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new BurstDetectionSettingsSnapshot(
            true,
            settings.GlobalEnabled,
            settings.AuthFailureEnabled,
            settings.AuthFailureThreshold,
            settings.AuthFailureWindowSeconds,
            settings.AuthFailureCooldownSeconds,
            settings.AuthFailureActionEligible,
            settings.RowVersion);
    }

    public BurstDetectionValues ValuesOrFallback(BurstDetectionOptions fallbackOptions) =>
        IsSeeded
            ? new BurstDetectionValues(
                GlobalEnabled,
                AuthFailureEnabled,
                AuthFailureThreshold,
                AuthFailureWindowSeconds,
                AuthFailureCooldownSeconds,
                AuthFailureActionEligible)
            : BurstDetectionValues.FromOptions(fallbackOptions);
}
