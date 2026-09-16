using Viegard.Application.Policy;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class PolicyThresholdSettingsTests
{
    [Theory]
    [InlineData(-0.01, 0.9)]
    [InlineData(0.7, 1.01)]
    [InlineData(double.NaN, 0.9)]
    public void Validator_rejects_confidence_values_outside_bounds(double reviewConfidence, double actionConfidence)
    {
        var valid = PolicyThresholdSettingsValidator.TryValidate(
            Settings(reviewConfidence: reviewConfidence, actionConfidence: actionConfidence),
            out var error);

        Assert.False(valid);
        Assert.Equal(PolicyThresholdSettingsValidator.ConfidenceBoundsError, error);
    }

    [Fact]
    public void Validator_rejects_review_confidence_above_action_confidence()
    {
        var valid = PolicyThresholdSettingsValidator.TryValidate(
            Settings(reviewConfidence: 0.95, actionConfidence: 0.9),
            out var error);

        Assert.False(valid);
        Assert.Equal(PolicyThresholdSettingsValidator.ConfidenceOrderError, error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validator_rejects_severity_outside_range(int severity)
    {
        var valid = PolicyThresholdSettingsValidator.TryValidate(
            Settings(actionMinSeverity: severity),
            out var error);

        Assert.False(valid);
        Assert.Equal(PolicyThresholdSettingsValidator.SeverityRangeError, error);
    }

    [Fact]
    public async Task Source_uses_policy_options_when_settings_are_unseeded()
    {
        var source = new PolicyThresholdSource(new InMemoryPolicyThresholdSettingsStore());
        var options = new PolicyOptions
        {
            AiReviewConfidence = 0.25,
            AiActionConfidence = 0.75,
            AiActionMinSeverity = 4,
        };

        await source.RefreshAsync();
        var values = source.CurrentValues(options);

        Assert.False(source.Current.IsSeeded);
        Assert.Equal(0.25, values.ReviewConfidence);
        Assert.Equal(0.75, values.ActionConfidence);
        Assert.Equal(4, values.ActionMinSeverity);
    }

    [Fact]
    public async Task Source_refresh_atomically_swaps_to_latest_snapshot()
    {
        var store = new InMemoryPolicyThresholdSettingsStore();
        var source = new PolicyThresholdSource(store);
        var now = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);

        var created = await store.UpdateAsync(
            Settings(reviewConfidence: 0.55, actionConfidence: 0.85, actionMinSeverity: 6),
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: now);
        Assert.True(created.Succeeded);
        await source.RefreshAsync();

        var first = source.CurrentValues(new PolicyOptions());
        Assert.True(source.Current.IsSeeded);
        Assert.Equal(0.55, first.ReviewConfidence);
        Assert.Equal(0.85, first.ActionConfidence);
        Assert.Equal(6, first.ActionMinSeverity);

        await store.UpdateAsync(
            created.Settings! with
            {
                ReviewConfidence = 0.65,
                ActionConfidence = 0.95,
                ActionMinSeverity = 8,
            },
            expectedRowVersion: created.Settings!.RowVersion,
            updatedBy: "hannah",
            updatedAt: now.AddMinutes(1));

        Assert.Equal(0.55, source.CurrentValues(new PolicyOptions()).ReviewConfidence);
        await source.RefreshAsync();
        var second = source.CurrentValues(new PolicyOptions());

        Assert.Equal(0.65, second.ReviewConfidence);
        Assert.Equal(0.95, second.ActionConfidence);
        Assert.Equal(8, second.ActionMinSeverity);
    }

    [Fact]
    public async Task Source_keeps_last_known_good_snapshot_on_refresh_failure()
    {
        var diagnostics = new RecordingDiagnostics();
        var store = new TogglePolicyThresholdStore(Settings(reviewConfidence: 0.6, actionConfidence: 0.9, actionMinSeverity: 7));
        var source = new PolicyThresholdSource(store, diagnostics);

        await source.RefreshAsync();
        store.ThrowOnGet = true;
        await source.RefreshAsync();

        Assert.Single(diagnostics.RefreshFailures);
        var values = source.CurrentValues(new PolicyOptions());
        Assert.True(source.Current.IsSeeded);
        Assert.Equal(0.6, values.ReviewConfidence);
        Assert.Equal(0.9, values.ActionConfidence);
        Assert.Equal(7, values.ActionMinSeverity);
    }

    [Fact]
    public async Task In_memory_store_creates_updates_and_reports_concurrency_conflicts()
    {
        var store = new InMemoryPolicyThresholdSettingsStore();
        var seededAt = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
        var first = await store.TryCreateAsync(Settings(reviewConfidence: 0.7, actionConfidence: 0.9, actionMinSeverity: 7) with
        {
            UpdatedAt = seededAt,
            UpdatedBy = PolicyThresholdSettings.SystemSeedActor,
        });

        Assert.True(first.Created);
        Assert.Equal(1, first.Settings.RowVersion);

        var second = await store.TryCreateAsync(Settings(reviewConfidence: 0.4, actionConfidence: 0.8, actionMinSeverity: 4) with
        {
            UpdatedAt = seededAt.AddMinutes(1),
            UpdatedBy = PolicyThresholdSettings.SystemSeedActor,
        });
        Assert.False(second.Created);
        Assert.Equal(0.7, second.Settings.ReviewConfidence);

        var updated = await store.UpdateAsync(
            first.Settings with
            {
                ReviewConfidence = 0.6,
                ActionConfidence = 0.95,
                ActionMinSeverity = 8,
            },
            expectedRowVersion: first.Settings.RowVersion,
            updatedBy: "operator",
            updatedAt: seededAt.AddMinutes(2));
        Assert.True(updated.Succeeded);
        var updatedSettings = updated.Settings!;
        Assert.Equal(2, updatedSettings.RowVersion);
        Assert.Equal("operator", updatedSettings.UpdatedBy);

        var conflict = await store.UpdateAsync(
            updatedSettings with { ReviewConfidence = 0.5 },
            expectedRowVersion: first.Settings.RowVersion,
            updatedBy: "stale",
            updatedAt: seededAt.AddMinutes(3));
        Assert.False(conflict.Succeeded);
        Assert.Equal(2, conflict.Settings!.RowVersion);
        Assert.Equal(0.6, conflict.Settings.ReviewConfidence);
    }

    private static PolicyThresholdSettings Settings(
        double reviewConfidence = 0.7,
        double actionConfidence = 0.9,
        int actionMinSeverity = 7) => new()
        {
            Id = PolicyThresholdSettings.FixedId,
            ReviewConfidence = reviewConfidence,
            ActionConfidence = actionConfidence,
            ActionMinSeverity = actionMinSeverity,
            RowVersion = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };

    private sealed class RecordingDiagnostics : IPolicyThresholdDiagnostics
    {
        public List<Exception> RefreshFailures { get; } = [];

        public void RefreshFailed(Exception exception) => RefreshFailures.Add(exception);
    }

    private sealed class TogglePolicyThresholdStore(PolicyThresholdSettings settings) : IPolicyThresholdSettingsStore
    {
        public bool ThrowOnGet { get; set; }

        public long CurrentChangeVersion => 0;

        public ValueTask<PolicyThresholdSettings?> GetAsync(CancellationToken cancellationToken = default) =>
            ThrowOnGet
                ? throw new InvalidOperationException("configured failure")
                : ValueTask.FromResult<PolicyThresholdSettings?>(settings);

        public ValueTask<PolicyThresholdSettingsCreateResult> TryCreateAsync(
            PolicyThresholdSettings candidate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PolicyThresholdSettingsSaveResult> UpdateAsync(
            PolicyThresholdSettings candidate,
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
