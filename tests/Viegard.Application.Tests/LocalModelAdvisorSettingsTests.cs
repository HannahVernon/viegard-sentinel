using Viegard.Application.Configuration;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class LocalModelAdvisorSettingsTests
{
    public static TheoryData<LocalModelAdvisorSettings, string> InvalidSettings => new()
    {
        { Valid() with { Endpoint = "not-a-url" }, LocalModelAdvisorSettingsValidator.EndpointError },
        { Valid() with { Model = "" }, LocalModelAdvisorSettingsValidator.ModelError },
        { Valid() with { Model = "bad\u0001model" }, LocalModelAdvisorSettingsValidator.ModelError },
        { Valid() with { Temperature = 2.1 }, LocalModelAdvisorSettingsValidator.TemperatureError },
        { Valid() with { TimeoutMs = 249 }, LocalModelAdvisorSettingsValidator.TimeoutError },
        { Valid() with { KeepAlive = "bad\u0001" }, LocalModelAdvisorSettingsValidator.KeepAliveError },
        { Valid() with { InvokeConfidenceMin = 0.9, InvokeConfidenceMax = 0.8 }, LocalModelAdvisorSettingsValidator.ConfidenceBandError },
        { Valid() with { InvokeConfidenceMin = -0.01 }, LocalModelAdvisorSettingsValidator.ConfidenceBandError },
        { Valid() with { MaxSeverityDelta = 11 }, LocalModelAdvisorSettingsValidator.MaxSeverityDeltaError },
        { Valid() with { MaxConfidenceDelta = 1.1 }, LocalModelAdvisorSettingsValidator.MaxConfidenceDeltaError },
    };

    [Fact]
    public void Validator_accepts_valid_defaults()
    {
        Assert.True(LocalModelAdvisorSettingsValidator.TryValidate(Valid(), out var error));
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Validator_rejects_invalid_fields(LocalModelAdvisorSettings settings, string expectedError)
    {
        Assert.False(LocalModelAdvisorSettingsValidator.TryValidate(settings, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public async Task In_memory_store_seeds_once_and_detects_optimistic_concurrency_conflict()
    {
        var store = new InMemoryLocalModelAdvisorSettingsStore();
        var seededAt = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

        var seeded = await store.SeedIfMissingAsync(new LocalModelAdvisorOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:11434/",
            Model = "qwen-test:latest",
            Temperature = 0.1,
            TimeoutMs = 1000,
            KeepAlive = "10m",
            InvokeConfidenceMin = 0.4,
            InvokeConfidenceMax = 0.8,
            MaxSeverityDelta = 2,
            MaxConfidenceDelta = 0.1,
        }, seededAt);

        Assert.NotNull(seeded);
        Assert.True(seeded!.Enabled);
        Assert.Equal("http://127.0.0.1:11434", seeded.Endpoint);
        Assert.Equal("qwen-test:latest", seeded.Model);
        Assert.Equal(1, seeded.Version);
        Assert.Equal(seededAt, seeded.SeededAt);

        var secondSeed = await store.SeedIfMissingAsync(new LocalModelAdvisorOptions
        {
            Enabled = false,
            Endpoint = "http://ignored.example",
            Model = "ignored",
        }, seededAt.AddMinutes(1));

        Assert.Equal("qwen-test:latest", secondSeed!.Model);
        Assert.True(secondSeed.Enabled);

        var conflict = await store.UpsertAsync(
            Valid() with { Model = "new-model" },
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: seededAt.AddMinutes(2));

        Assert.False(conflict.Succeeded);
        Assert.Equal("qwen-test:latest", conflict.Settings!.Model);
    }

    private static LocalModelAdvisorSettings Valid() => new()
    {
        Enabled = false,
        Endpoint = LocalModelAdvisorSettings.DefaultEndpoint,
        Model = LocalModelAdvisorSettings.DefaultModel,
        Temperature = 0.0,
        TimeoutMs = 8000,
        KeepAlive = LocalModelAdvisorSettings.DefaultKeepAlive,
        InvokeConfidenceMin = 0.50,
        InvokeConfidenceMax = 0.85,
        MaxSeverityDelta = 3,
        MaxConfidenceDelta = 0.20,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };
}
