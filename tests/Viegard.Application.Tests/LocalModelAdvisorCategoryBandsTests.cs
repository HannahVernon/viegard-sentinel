using Viegard.Application.Configuration;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class LocalModelAdvisorCategoryBandsTests
{
    public static TheoryData<LocalModelAdvisorCategoryBand, string> InvalidBands => new()
    {
        { Valid() with { Category = "" }, LocalModelAdvisorCategoryBandValidator.CategoryError },
        { Valid() with { Category = "bad\u0001category" }, LocalModelAdvisorCategoryBandValidator.CategoryError },
        { Valid() with { Category = new string('a', LocalModelAdvisorCategoryBand.MaxCategoryLength + 1) }, LocalModelAdvisorCategoryBandValidator.CategoryError },
        { Valid() with { InvokeConfidenceMin = -0.01 }, LocalModelAdvisorSettingsValidator.ConfidenceBandError },
        { Valid() with { InvokeConfidenceMax = 1.01 }, LocalModelAdvisorSettingsValidator.ConfidenceBandError },
        { Valid() with { InvokeConfidenceMin = 0.8, InvokeConfidenceMax = 0.7 }, LocalModelAdvisorSettingsValidator.ConfidenceBandError },
        { Valid() with { MaxSeverityDelta = 11 }, LocalModelAdvisorSettingsValidator.MaxSeverityDeltaError },
        { Valid() with { MaxConfidenceDelta = 1.1 }, LocalModelAdvisorSettingsValidator.MaxConfidenceDeltaError },
        { Valid() with { MaxDownwardSeverityDelta = 11 }, LocalModelAdvisorSettingsValidator.MaxDownwardSeverityDeltaError },
        { Valid() with { MaxDownwardConfidenceDelta = double.NaN }, LocalModelAdvisorSettingsValidator.MaxDownwardConfidenceDeltaError },
    };

    [Fact]
    public void Validator_accepts_valid_partial_override()
    {
        Assert.True(LocalModelAdvisorCategoryBandValidator.TryValidate(Valid(), out var error));
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [MemberData(nameof(InvalidBands))]
    public void Validator_rejects_invalid_fields(LocalModelAdvisorCategoryBand band, string expectedError)
    {
        Assert.False(LocalModelAdvisorCategoryBandValidator.TryValidate(band, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Validator_rejects_effective_min_greater_than_inherited_max()
    {
        var band = Valid() with
        {
            InvokeConfidenceMin = 0.9,
            InvokeConfidenceMax = null,
        };
        var inherited = Global() with { InvokeConfidenceMax = 0.8 };

        Assert.False(LocalModelAdvisorCategoryBandValidator.TryValidateEffective(band, inherited, out var error));
        Assert.Equal(LocalModelAdvisorSettingsValidator.ConfidenceBandError, error);
    }

    [Fact]
    public async Task Resolver_merges_overrides_and_inherits_null_fields()
    {
        var store = new InMemoryLocalModelAdvisorCategoryBandStore();
        await store.UpsertAsync(Valid() with
        {
            Enabled = false,
            InvokeConfidenceMin = null,
            InvokeConfidenceMax = 0.75,
            MaxSeverityDelta = 1,
            MaxConfidenceDelta = null,
            DeEscalationEnabled = true,
            MaxDownwardSeverityDelta = 2,
            MaxDownwardConfidenceDelta = null,
        }, 0, "hannah", DateTimeOffset.UtcNow);
        var source = new LocalModelAdvisorCategoryBandSource(store);
        await source.RefreshAsync();

        var resolved = source.Resolve(Global(), "path-traversal");

        Assert.False(resolved.Enabled);
        Assert.Equal(0.5, resolved.InvokeConfidenceMin);
        Assert.Equal(0.75, resolved.InvokeConfidenceMax);
        Assert.Equal(1, resolved.MaxSeverityDelta);
        Assert.Equal(0.2, resolved.MaxConfidenceDelta);
        Assert.True(resolved.DeEscalationEnabled);
        Assert.Equal(2, resolved.MaxDownwardSeverityDelta);
        Assert.Equal(0.1, resolved.MaxDownwardConfidenceDelta);
    }

    [Fact]
    public async Task In_memory_store_supports_upsert_conflict_list_get_and_delete()
    {
        var store = new InMemoryLocalModelAdvisorCategoryBandStore();
        var now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

        var created = await store.UpsertAsync(Valid(), 0, "hannah", now);

        Assert.True(created.Succeeded);
        Assert.Equal(1, created.Band!.Version);
        Assert.Equal("hannah", created.Band.UpdatedBy);
        Assert.Equal("path-traversal", (await store.GetAsync(" path-traversal "))!.Category);
        Assert.Single(await store.ListAsync());

        var updated = await store.UpsertAsync(
            created.Band with { Enabled = true },
            created.Band.Version,
            "operator",
            now.AddMinutes(1));

        Assert.True(updated.Succeeded);
        Assert.Equal(2, updated.Band!.Version);
        Assert.True(updated.Band.Enabled);

        var conflict = await store.UpsertAsync(
            updated.Band with { MaxSeverityDelta = 9 },
            created.Band.Version,
            "stale",
            now.AddMinutes(2));

        Assert.False(conflict.Succeeded);
        Assert.Equal(2, conflict.Band!.Version);

        var deleteConflict = await store.DeleteAsync("path-traversal", created.Band.Version, "stale", now.AddMinutes(3));
        Assert.Equal(LocalModelAdvisorCategoryBandDeleteStatus.Conflict, deleteConflict.Status);

        var deleted = await store.DeleteAsync("path-traversal", updated.Band.Version, "operator", now.AddMinutes(4));
        Assert.True(deleted.Succeeded);
        Assert.Null(await store.GetAsync("path-traversal"));
        Assert.Empty(await store.ListAsync());
    }

    private static LocalModelAdvisorCategoryBand Valid() => new()
    {
        Category = "path-traversal",
        Enabled = null,
        InvokeConfidenceMin = 0.4,
        InvokeConfidenceMax = null,
        MaxSeverityDelta = 2,
        MaxConfidenceDelta = null,
        DeEscalationEnabled = null,
        MaxDownwardSeverityDelta = 1,
        MaxDownwardConfidenceDelta = null,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };

    private static LocalModelAdvisorValues Global() => new(
        true,
        LocalModelAdvisorSettings.DefaultEndpoint,
        LocalModelAdvisorSettings.DefaultModel,
        0.0,
        8000,
        LocalModelAdvisorSettings.DefaultKeepAlive,
        0.5,
        0.85,
        3,
        0.2,
        DeEscalationEnabled: false,
        MaxDownwardSeverityDelta: 1,
        MaxDownwardConfidenceDelta: 0.1);
}
