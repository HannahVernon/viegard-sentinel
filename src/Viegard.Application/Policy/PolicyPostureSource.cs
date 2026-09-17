namespace Viegard.Application.Policy;

public interface IPolicyPostureDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullPolicyPostureDiagnostics : IPolicyPostureDiagnostics
{
    public static NullPolicyPostureDiagnostics Instance { get; } = new();

    private NullPolicyPostureDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

/// <summary>
/// Last-known-good snapshot of the database-owned policy posture, refreshed
/// via LISTEN/NOTIFY with a polling fallback.  Falls back to the environment
/// posture until the settings row is seeded, so posture consumers never see
/// an absent value.
/// </summary>
public sealed class PolicyPostureSource(
    IPolicyPostureSettingsStore? settingsStore = null,
    IPolicyPostureDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly IPolicyPostureDiagnostics _diagnostics = diagnostics ?? NullPolicyPostureDiagnostics.Instance;
    private PolicyPostureSnapshot _snapshot = PolicyPostureSnapshot.Unseeded;

    public PolicyPostureSnapshot Current => Volatile.Read(ref _snapshot);

    public PolicyPostureValues CurrentValues(PolicyOptions fallbackOptions) =>
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
                    ? PolicyPostureSnapshot.Unseeded
                    : PolicyPostureSnapshot.Seeded(settings));
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

public sealed record PolicyPostureValues(bool DryRun, bool ManualApprovalMode, bool EmergencyStop)
{
    public static PolicyPostureValues FromOptions(PolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PolicyPostureValues(
            options.Posture.DryRun,
            options.Posture.ManualApprovalMode,
            options.Posture.EmergencyStop);
    }
}

public sealed record PolicyPostureSnapshot(
    bool IsSeeded,
    bool DryRun,
    bool ManualApprovalMode,
    bool EmergencyStop,
    int RowVersion)
{
    public static PolicyPostureSnapshot Unseeded { get; } = new(
        false,
        true,
        true,
        false,
        0);

    public static PolicyPostureSnapshot Seeded(PolicyPostureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PolicyPostureSnapshot(
            true,
            settings.DryRun,
            settings.ManualApprovalMode,
            settings.EmergencyStop,
            settings.RowVersion);
    }

    public PolicyPostureValues ValuesOrFallback(PolicyOptions fallbackOptions) =>
        IsSeeded
            ? new PolicyPostureValues(DryRun, ManualApprovalMode, EmergencyStop)
            : PolicyPostureValues.FromOptions(fallbackOptions);
}
