namespace Viegard.Application.Classifiers;

public interface IClassifierSettingsDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullClassifierSettingsDiagnostics : IClassifierSettingsDiagnostics
{
    public static NullClassifierSettingsDiagnostics Instance { get; } = new();

    private NullClassifierSettingsDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

public sealed class ClassifierSettingsSource(
    IClassifierSettingsStore? settingsStore = null,
    IClassifierSettingsDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly IClassifierSettingsDiagnostics _diagnostics = diagnostics ?? NullClassifierSettingsDiagnostics.Instance;
    private ClassifierSettingsSnapshot _snapshot = ClassifierSettingsSnapshot.Unseeded;

    public ClassifierSettingsSnapshot Current => Volatile.Read(ref _snapshot);

    public ClassifierValues CurrentValues(ClassifierOptions fallbackOptions) =>
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
                    ? ClassifierSettingsSnapshot.Unseeded
                    : ClassifierSettingsSnapshot.Seeded(settings));
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

public sealed record ClassifierSettingsSnapshot(
    bool IsSeeded,
    double ScoreForFullConfidence,
    double SeverityPerScorePoint,
    double BlockRecommendationScore,
    int RepeatConfidenceMinEvents,
    double RepeatConfidenceCoefficient,
    double RepeatConfidenceBonusCap,
    int RowVersion)
{
    public static ClassifierSettingsSnapshot Unseeded { get; } = new(
        false,
        0.0,
        0.0,
        0.0,
        0,
        0.0,
        0.0,
        0);

    public static ClassifierSettingsSnapshot Seeded(ClassifierSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ClassifierSettingsSnapshot(
            true,
            settings.ScoreForFullConfidence,
            settings.SeverityPerScorePoint,
            settings.BlockRecommendationScore,
            settings.RepeatConfidenceMinEvents,
            settings.RepeatConfidenceCoefficient,
            settings.RepeatConfidenceBonusCap,
            settings.RowVersion);
    }

    public ClassifierValues ValuesOrFallback(ClassifierOptions fallbackOptions) =>
        IsSeeded
            ? new ClassifierValues(
                ScoreForFullConfidence,
                SeverityPerScorePoint,
                BlockRecommendationScore,
                RepeatConfidenceMinEvents,
                RepeatConfidenceCoefficient,
                RepeatConfidenceBonusCap)
            : ClassifierValues.FromOptions(fallbackOptions);
}
