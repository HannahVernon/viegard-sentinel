using Viegard.Application.Classifiers;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class ClassifierSettingsTests
{
    [Theory]
    [InlineData(0.0, 2.0, 3.0)]
    [InlineData(5.0, -1.0, 3.0)]
    [InlineData(5.0, 2.0, double.PositiveInfinity)]
    public void Validator_rejects_non_positive_or_non_finite_score_controls(
        double scoreForFullConfidence,
        double severityPerScorePoint,
        double blockRecommendationScore)
    {
        var valid = ClassifierSettingsValidator.TryValidate(
            Settings(
                scoreForFullConfidence: scoreForFullConfidence,
                severityPerScorePoint: severityPerScorePoint,
                blockRecommendationScore: blockRecommendationScore),
            out var error);

        Assert.False(valid);
        Assert.Equal(ClassifierSettingsValidator.PositiveScoreError, error);
    }

    [Fact]
    public void Validator_rejects_repeat_min_events_below_one()
    {
        var valid = ClassifierSettingsValidator.TryValidate(
            Settings(repeatConfidenceMinEvents: 0),
            out var error);

        Assert.False(valid);
        Assert.Equal(ClassifierSettingsValidator.RepeatMinEventsError, error);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(double.PositiveInfinity)]
    public void Validator_rejects_invalid_repeat_coefficient(double coefficient)
    {
        var valid = ClassifierSettingsValidator.TryValidate(
            Settings(repeatConfidenceCoefficient: coefficient),
            out var error);

        Assert.False(valid);
        Assert.Equal(ClassifierSettingsValidator.RepeatCoefficientError, error);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Validator_rejects_repeat_bonus_cap_outside_bounds(double cap)
    {
        var valid = ClassifierSettingsValidator.TryValidate(
            Settings(repeatConfidenceBonusCap: cap),
            out var error);

        Assert.False(valid);
        Assert.Equal(ClassifierSettingsValidator.RepeatBonusCapError, error);
    }

    [Fact]
    public async Task Source_uses_classifier_options_when_settings_are_unseeded()
    {
        var source = new ClassifierSettingsSource(new InMemoryClassifierSettingsStore());
        var options = new ClassifierOptions
        {
            ScoreForFullConfidence = 4.0,
            SeverityPerScorePoint = 1.5,
            BlockRecommendationScore = 2.5,
            RepeatConfidenceMinEvents = 3,
            RepeatConfidenceCoefficient = 0.05,
            RepeatConfidenceBonusCap = 0.20,
        };

        await source.RefreshAsync();
        var values = source.CurrentValues(options);

        Assert.False(source.Current.IsSeeded);
        Assert.Equal(4.0, values.ScoreForFullConfidence);
        Assert.Equal(1.5, values.SeverityPerScorePoint);
        Assert.Equal(2.5, values.BlockRecommendationScore);
        Assert.Equal(3, values.RepeatConfidenceMinEvents);
        Assert.Equal(0.05, values.RepeatConfidenceCoefficient);
        Assert.Equal(0.20, values.RepeatConfidenceBonusCap);
    }

    [Fact]
    public async Task Source_refresh_atomically_swaps_to_latest_snapshot()
    {
        var store = new InMemoryClassifierSettingsStore();
        var source = new ClassifierSettingsSource(store);
        var now = new DateTimeOffset(2026, 9, 22, 14, 0, 0, TimeSpan.Zero);

        var created = await store.UpdateAsync(
            Settings(scoreForFullConfidence: 4.0, severityPerScorePoint: 1.5, blockRecommendationScore: 2.5),
            expectedRowVersion: 0,
            updatedBy: "operator",
            updatedAt: now);
        Assert.True(created.Succeeded);
        await source.RefreshAsync();

        var first = source.CurrentValues(new ClassifierOptions());
        Assert.True(source.Current.IsSeeded);
        Assert.Equal(4.0, first.ScoreForFullConfidence);
        Assert.Equal(1.5, first.SeverityPerScorePoint);
        Assert.Equal(2.5, first.BlockRecommendationScore);

        await store.UpdateAsync(
            created.Settings! with
            {
                ScoreForFullConfidence = 6.0,
                SeverityPerScorePoint = 2.5,
                BlockRecommendationScore = 3.5,
                RepeatConfidenceMinEvents = 8,
                RepeatConfidenceCoefficient = 0.07,
                RepeatConfidenceBonusCap = 0.25,
            },
            expectedRowVersion: created.Settings!.RowVersion,
            updatedBy: "operator",
            updatedAt: now.AddMinutes(1));

        Assert.Equal(4.0, source.CurrentValues(new ClassifierOptions()).ScoreForFullConfidence);
        await source.RefreshAsync();
        var second = source.CurrentValues(new ClassifierOptions());

        Assert.Equal(6.0, second.ScoreForFullConfidence);
        Assert.Equal(2.5, second.SeverityPerScorePoint);
        Assert.Equal(3.5, second.BlockRecommendationScore);
        Assert.Equal(8, second.RepeatConfidenceMinEvents);
        Assert.Equal(0.07, second.RepeatConfidenceCoefficient);
        Assert.Equal(0.25, second.RepeatConfidenceBonusCap);
    }

    [Fact]
    public async Task Source_keeps_last_known_good_snapshot_on_refresh_failure()
    {
        var diagnostics = new RecordingDiagnostics();
        var store = new ToggleClassifierSettingsStore(Settings(scoreForFullConfidence: 4.0, repeatConfidenceBonusCap: 0.25));
        var source = new ClassifierSettingsSource(store, diagnostics);

        await source.RefreshAsync();
        store.ThrowOnGet = true;
        await source.RefreshAsync();

        Assert.Single(diagnostics.RefreshFailures);
        var values = source.CurrentValues(new ClassifierOptions());
        Assert.True(source.Current.IsSeeded);
        Assert.Equal(4.0, values.ScoreForFullConfidence);
        Assert.Equal(0.25, values.RepeatConfidenceBonusCap);
    }

    [Fact]
    public async Task In_memory_store_creates_updates_and_reports_concurrency_conflicts()
    {
        var store = new InMemoryClassifierSettingsStore();
        var seededAt = new DateTimeOffset(2026, 9, 22, 14, 0, 0, TimeSpan.Zero);
        var first = await store.TryCreateAsync(Settings(scoreForFullConfidence: 4.0) with
        {
            UpdatedAt = seededAt,
            UpdatedBy = ClassifierSettings.SystemSeedActor,
        });

        Assert.True(first.Created);
        Assert.Equal(1, first.Settings.RowVersion);

        var second = await store.TryCreateAsync(Settings(scoreForFullConfidence: 7.0) with
        {
            UpdatedAt = seededAt.AddMinutes(1),
            UpdatedBy = ClassifierSettings.SystemSeedActor,
        });
        Assert.False(second.Created);
        Assert.Equal(4.0, second.Settings.ScoreForFullConfidence);

        var updated = await store.UpdateAsync(
            first.Settings with
            {
                ScoreForFullConfidence = 6.0,
                RepeatConfidenceMinEvents = 8,
                RepeatConfidenceCoefficient = 0.07,
                RepeatConfidenceBonusCap = 0.25,
            },
            expectedRowVersion: first.Settings.RowVersion,
            updatedBy: "operator",
            updatedAt: seededAt.AddMinutes(2));
        Assert.True(updated.Succeeded);
        var updatedSettings = updated.Settings!;
        Assert.Equal(2, updatedSettings.RowVersion);
        Assert.Equal("operator", updatedSettings.UpdatedBy);
        Assert.Equal(8, updatedSettings.RepeatConfidenceMinEvents);

        var conflict = await store.UpdateAsync(
            updatedSettings with { ScoreForFullConfidence = 5.0 },
            expectedRowVersion: first.Settings.RowVersion,
            updatedBy: "stale",
            updatedAt: seededAt.AddMinutes(3));
        Assert.False(conflict.Succeeded);
        Assert.Equal(2, conflict.Settings!.RowVersion);
        Assert.Equal(6.0, conflict.Settings.ScoreForFullConfidence);
    }

    private static ClassifierSettings Settings(
        double scoreForFullConfidence = 5.0,
        double severityPerScorePoint = 2.0,
        double blockRecommendationScore = 3.0,
        int repeatConfidenceMinEvents = 4,
        double repeatConfidenceCoefficient = 0.08,
        double repeatConfidenceBonusCap = 0.30) => new()
        {
            Id = ClassifierSettings.FixedId,
            ScoreForFullConfidence = scoreForFullConfidence,
            SeverityPerScorePoint = severityPerScorePoint,
            BlockRecommendationScore = blockRecommendationScore,
            RepeatConfidenceMinEvents = repeatConfidenceMinEvents,
            RepeatConfidenceCoefficient = repeatConfidenceCoefficient,
            RepeatConfidenceBonusCap = repeatConfidenceBonusCap,
            RowVersion = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };

    private sealed class RecordingDiagnostics : IClassifierSettingsDiagnostics
    {
        public List<Exception> RefreshFailures { get; } = [];

        public void RefreshFailed(Exception exception) => RefreshFailures.Add(exception);
    }

    private sealed class ToggleClassifierSettingsStore(ClassifierSettings settings) : IClassifierSettingsStore
    {
        public bool ThrowOnGet { get; set; }

        public long CurrentChangeVersion => 0;

        public ValueTask<ClassifierSettings?> GetAsync(CancellationToken cancellationToken = default) =>
            ThrowOnGet
                ? throw new InvalidOperationException("configured failure")
                : ValueTask.FromResult<ClassifierSettings?>(settings);

        public ValueTask<ClassifierSettingsCreateResult> TryCreateAsync(
            ClassifierSettings candidate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ClassifierSettingsSaveResult> UpdateAsync(
            ClassifierSettings candidate,
            int expectedRowVersion,
            string updatedBy,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<long> WaitForChangeAsync(
            long lastSeenVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentChangeVersion);
    }
}
