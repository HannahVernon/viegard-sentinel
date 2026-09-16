namespace Viegard.Application.Policy;

public interface IPolicyThresholdDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullPolicyThresholdDiagnostics : IPolicyThresholdDiagnostics
{
    public static NullPolicyThresholdDiagnostics Instance { get; } = new();

    private NullPolicyThresholdDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

public sealed class PolicyThresholdSource(
    IPolicyThresholdSettingsStore? settingsStore = null,
    IPolicyThresholdDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly IPolicyThresholdDiagnostics _diagnostics = diagnostics ?? NullPolicyThresholdDiagnostics.Instance;
    private PolicyThresholdSnapshot _snapshot = PolicyThresholdSnapshot.Unseeded;

    public PolicyThresholdSnapshot Current => Volatile.Read(ref _snapshot);

    public PolicyThresholdValues CurrentValues(PolicyOptions fallbackOptions) =>
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
                    ? PolicyThresholdSnapshot.Unseeded
                    : PolicyThresholdSnapshot.Seeded(settings));
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

public sealed record PolicyThresholdSnapshot(
    bool IsSeeded,
    double ReviewConfidence,
    double ActionConfidence,
    int ActionMinSeverity,
    int RowVersion)
{
    public static PolicyThresholdSnapshot Unseeded { get; } = new(
        false,
        0.0,
        0.0,
        0,
        0);

    public static PolicyThresholdSnapshot Seeded(PolicyThresholdSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PolicyThresholdSnapshot(
            true,
            settings.ReviewConfidence,
            settings.ActionConfidence,
            settings.ActionMinSeverity,
            settings.RowVersion);
    }

    public PolicyThresholdValues ValuesOrFallback(PolicyOptions fallbackOptions) =>
        IsSeeded
            ? new PolicyThresholdValues(ReviewConfidence, ActionConfidence, ActionMinSeverity)
            : PolicyThresholdValues.FromOptions(fallbackOptions);
}
