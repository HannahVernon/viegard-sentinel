using Viegard.Application.Configuration;
using Viegard.Domain;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class LocalModelAdvisorConsultStoreTests
{
    [Fact]
    public async Task In_memory_store_aggregates_recent_and_prunes()
    {
        var store = new InMemoryLocalModelAdvisorConsultStore();
        var classificationId = ViegardId.New();
        var now = new DateTimeOffset(2026, 9, 20, 7, 0, 0, TimeSpan.Zero);

        await store.AppendAsync(Record(classificationId, AdvisorConsultOutcome.Escalated, now.AddMinutes(-10), 100, finalSeverity: 8));
        await store.AppendAsync(Record(ViegardId.New(), AdvisorConsultOutcome.NoChange, now.AddMinutes(-9), 200));
        await store.AppendAsync(Record(ViegardId.New(), AdvisorConsultOutcome.NoChange, now.AddMinutes(-8), 300));
        await store.AppendAsync(Record(ViegardId.New(), AdvisorConsultOutcome.ProviderFailed, now.AddHours(-2), 400, failureKind: "Timeout"));
        await store.AppendAsync(Record(ViegardId.New(), AdvisorConsultOutcome.SkippedOutOfBand, now.AddDays(-2), null));

        var consult = await store.GetByClassificationIdAsync(classificationId);
        Assert.NotNull(consult);
        Assert.Equal(AdvisorConsultOutcome.Escalated, consult!.Outcome);

        var counts = await store.GetOutcomeCountsAsync(now.AddHours(-1));
        Assert.Equal(1, counts.Single(c => c.Outcome == AdvisorConsultOutcome.Escalated).Count);
        Assert.Equal(2, counts.Single(c => c.Outcome == AdvisorConsultOutcome.NoChange).Count);
        Assert.DoesNotContain(counts, c => c.Outcome == AdvisorConsultOutcome.ProviderFailed);

        var latency = await store.GetLatencyStatsAsync(now.AddHours(-1));
        Assert.Equal(3, latency.Count);
        Assert.Equal(200, latency.P50);
        Assert.Equal(300, latency.P95);

        var recent = await store.GetRecentAsync(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal(AdvisorConsultOutcome.NoChange, recent[0].Outcome);

        Assert.Equal(1, await store.PruneOlderThanAsync(now.AddDays(-1)));
        Assert.Equal(4, (await store.GetRecentAsync(10)).Count);
    }

    [Fact]
    public async Task In_memory_store_pages_and_filters_by_outcome_and_gets_by_id()
    {
        var store = new InMemoryLocalModelAdvisorConsultStore();
        var now = new DateTimeOffset(2026, 9, 20, 7, 0, 0, TimeSpan.Zero);
        var escalated = Record(ViegardId.New(), AdvisorConsultOutcome.Escalated, now.AddMinutes(-1), 100, finalSeverity: 8);
        await store.AppendAsync(escalated);
        await store.AppendAsync(Record(ViegardId.New(), AdvisorConsultOutcome.NoChange, now.AddMinutes(-2), 200));
        await store.AppendAsync(Record(ViegardId.New(), AdvisorConsultOutcome.NoChange, now.AddMinutes(-3), 300));
        await store.AppendAsync(Record(ViegardId.New(), AdvisorConsultOutcome.ProviderFailed, now.AddMinutes(-4), 400, failureKind: "Timeout"));

        var firstPage = await store.ListPageAsync(beforeId: null, pageSize: 2, outcome: null);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(4, firstPage.TotalCount);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await store.ListPageAsync(firstPage.NextCursor, pageSize: 2, outcome: null);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Null(secondPage.NextCursor);
        Assert.Equal(2, secondPage.Preceding);

        var noChangeOnly = await store.ListPageAsync(beforeId: null, pageSize: 10, outcome: AdvisorConsultOutcome.NoChange);
        Assert.Equal(2, noChangeOnly.Items.Count);
        Assert.All(noChangeOnly.Items, r => Assert.Equal(AdvisorConsultOutcome.NoChange, r.Outcome));
        Assert.Equal(2, noChangeOnly.TotalCount);

        var byId = await store.GetByIdAsync(escalated.Id);
        Assert.NotNull(byId);
        Assert.Equal(AdvisorConsultOutcome.Escalated, byId!.Outcome);
        Assert.Null(await store.GetByIdAsync(ViegardId.New()));
    }

    private static AdvisorConsultRecord Record(
        Guid classificationId,
        AdvisorConsultOutcome outcome,
        DateTimeOffset createdAt,
        int? latencyMs,
        int finalSeverity = 6,
        string? failureKind = null) => new()
        {
            ClassificationId = classificationId,
            IncidentId = ViegardId.New(),
            Category = "path-traversal",
            Outcome = outcome,
            BaseSeverity = 6,
            FinalSeverity = finalSeverity,
            BaseConfidence = 0.6,
            FinalConfidence = finalSeverity > 6 ? 0.8 : 0.6,
            LatencyMs = latencyMs,
            FailureKind = failureKind,
            ModelId = "qwen-test:latest",
            CreatedAt = createdAt,
        };
}
