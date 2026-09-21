using Viegard.Application.Configuration;
using Viegard.Application.Inference;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class LocalModelAdvisorResponseCacheTests
{
    [Fact]
    public void Cache_key_is_stable_for_same_semantic_inputs()
    {
        var variables = Variables();

        var first = LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-a", variables);
        var second = LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-a", variables);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Cache_key_changes_when_semantic_inputs_change()
    {
        var baseline = LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-a", Variables());

        Assert.NotEqual(baseline, LocalModelAdvisorResponseCacheKey.Build("template-v2", "model-a", Variables()));
        Assert.NotEqual(baseline, LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-b", Variables()));
        Assert.NotEqual(baseline, LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-a", Variables(value: "different")));
        Assert.NotEqual(baseline, LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-a", Variables(trust: PromptTrust.System)));
        Assert.NotEqual(baseline, LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-a", Variables(name: "other")));
    }

    [Fact]
    public void Cache_key_changes_between_single_model_and_ensemble_identity()
    {
        var variables = Variables();
        var single = LocalModelAdvisorResponseCacheKey.Build("template-v1", "model-a", variables);
        var ensemble = LocalModelAdvisorResponseCacheKey.Build(
            "template-v1",
            "ensemble:avg|model-a@http://127.0.0.1:11434|model-b@http://127.0.0.2:11434",
            variables);

        Assert.NotEqual(single, ensemble);
    }

    [Fact]
    public async Task In_memory_store_returns_only_non_expired_entries_and_prunes_expired()
    {
        var store = new InMemoryLocalModelAdvisorResponseCacheStore();
        var now = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        await store.SetAsync(Entry("live", now, now.AddHours(1)));
        await store.SetAsync(Entry("expired", now.AddHours(-2), now.AddHours(-1)));

        Assert.NotNull(await store.GetAsync("live", now));
        Assert.Null(await store.GetAsync("expired", now));
        Assert.Equal(1, await store.PruneExpiredAsync(now));
        Assert.NotNull(await store.GetAsync("live", now));
    }

    [Fact]
    public async Task In_memory_store_upserts_by_cache_key()
    {
        var store = new InMemoryLocalModelAdvisorResponseCacheStore();
        var now = DateTimeOffset.UtcNow;
        await store.SetAsync(Entry("same", now, now.AddHours(1)) with { Severity = 4 });
        await store.SetAsync(Entry("same", now, now.AddHours(1)) with { Severity = 8 });

        var restored = await store.GetAsync("same", now);

        Assert.NotNull(restored);
        Assert.Equal(8, restored!.Severity);
    }

    private static IReadOnlyList<PromptVariable> Variables(
        string name = "evidence_1",
        PromptTrust trust = PromptTrust.UntrustedObservedData,
        string value = "observed") =>
        [
            new PromptVariable
            {
                Name = name,
                Trust = trust,
                Value = value,
            },
        ];

    private static LocalModelAdvisorResponseCacheEntry Entry(
        string cacheKey,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) => new()
        {
            CacheKey = cacheKey,
            ModelId = "model-a",
            TemplateVersion = "template-v1",
            Severity = 7,
            Confidence = 0.7,
            Reasons = ["cached"],
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
}
