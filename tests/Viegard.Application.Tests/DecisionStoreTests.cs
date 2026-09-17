using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class DecisionStoreTests
{
    [Fact]
    public async Task TryReviewAsync_updates_only_unreviewed_requireapproval_decisions()
    {
        var store = new InMemoryDecisionStore();
        var now = DateTimeOffset.UtcNow;
        var permit = Decision(DecisionOutcome.ActionAuthorized);
        var reviewed = Decision(DecisionOutcome.RequireApproval) with
        {
            ReviewedBy = "hannah",
            ReviewedAt = now.AddMinutes(-1),
            ReviewOutcome = DecisionReviewOutcome.Approved,
        };
        var pending = Decision(DecisionOutcome.RequireApproval);
        await store.AddAsync(permit);
        await store.AddAsync(reviewed);
        await store.AddAsync(pending);

        Assert.Null(await store.TryReviewAsync(permit.Id, DecisionReviewOutcome.Approved, "operator", now));
        Assert.Null(await store.TryReviewAsync(reviewed.Id, DecisionReviewOutcome.Rejected, "operator", now));

        var claimed = await store.TryReviewAsync(pending.Id, DecisionReviewOutcome.Rejected, "operator", now);

        Assert.NotNull(claimed);
        Assert.Equal(DecisionReviewOutcome.Rejected, claimed!.ReviewOutcome);
        Assert.Equal("operator", claimed.ReviewedBy);
        Assert.Equal(now.ToUniversalTime(), claimed.ReviewedAt);
    }

    [Fact]
    public async Task TryReviewAsync_allows_only_one_race_winner()
    {
        var store = new InMemoryDecisionStore();
        var decision = Decision(DecisionOutcome.RequireApproval);
        await store.AddAsync(decision);

        var attempts = Enumerable.Range(0, 16)
            .Select(index => Task.Run(async () =>
                await store.TryReviewAsync(
                    decision.Id,
                    index % 2 == 0 ? DecisionReviewOutcome.Approved : DecisionReviewOutcome.Rejected,
                    $"operator-{index}",
                    DateTimeOffset.UtcNow)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result is not null);
        var saved = await store.GetAsync(decision.Id);
        Assert.NotNull(saved!.ReviewedAt);
        Assert.NotNull(saved.ReviewOutcome);
    }

    [Fact]
    public async Task ListPageAsync_filters_by_minimum_severity()
    {
        var classifications = new InMemoryClassificationStore();
        var store = new InMemoryDecisionStore(classifications);
        var low = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 2);
        var high = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 8);
        await store.AddAsync(Decision(DecisionOutcome.RequireApproval));

        var page = await store.ListPageAsync(null, 10, new DecisionListFilter(null, null, MinSeverity: 5));

        Assert.Single(page.Items);
        Assert.Equal(high.Id, page.Items[0].Id);

        var all = await store.ListPageAsync(null, 10, new DecisionListFilter(null, null, MinSeverity: 1));
        Assert.Equal(2, all.Items.Count);
        Assert.Contains(all.Items, d => d.Id == low.Id);
    }

    [Fact]
    public async Task ListPageAsync_sorts_by_severity()
    {
        var classifications = new InMemoryClassificationStore();
        var store = new InMemoryDecisionStore(classifications);
        var low = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 2);
        var high = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 9);
        var mid = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 5);

        var page = await store.ListPageAsync(
            null,
            10,
            sort: new ListSort<DecisionSortColumn>(DecisionSortColumn.Severity, SortDirection.Desc));

        Assert.Equal([high.Id, mid.Id, low.Id], page.Items.Select(d => d.Id).ToArray());

        var ascending = await store.ListPageAsync(
            null,
            10,
            sort: new ListSort<DecisionSortColumn>(DecisionSortColumn.Severity, SortDirection.Asc));
        Assert.Equal([low.Id, mid.Id, high.Id], ascending.Items.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task BulkRejectUnreviewedAsync_rejects_only_matching_decisions()
    {
        var classifications = new InMemoryClassificationStore();
        var store = new InMemoryDecisionStore(classifications);
        var now = DateTimeOffset.UtcNow;
        var lowPending = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 2);
        var highPending = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 8);
        var lowAuthorized = await AddWithSeverityAsync(store, classifications, DecisionOutcome.ActionAuthorized, 2);
        var lowReviewed = await AddWithSeverityAsync(store, classifications, DecisionOutcome.RequireApproval, 2);
        await store.TryReviewAsync(lowReviewed.Id, DecisionReviewOutcome.Approved, "hannah", now.AddMinutes(-5));
        var missingClassification = Decision(DecisionOutcome.RequireApproval);
        await store.AddAsync(missingClassification);

        var rejected = await store.BulkRejectUnreviewedAsync(3, "operator", now);

        Assert.Equal(1, rejected);
        var updated = await store.GetAsync(lowPending.Id);
        Assert.Equal(DecisionReviewOutcome.Rejected, updated!.ReviewOutcome);
        Assert.Equal("operator", updated.ReviewedBy);
        Assert.Equal(now.ToUniversalTime(), updated.ReviewedAt);
        Assert.Null((await store.GetAsync(highPending.Id))!.ReviewedAt);
        Assert.Null((await store.GetAsync(lowAuthorized.Id))!.ReviewedAt);
        Assert.Equal(DecisionReviewOutcome.Approved, (await store.GetAsync(lowReviewed.Id))!.ReviewOutcome);
        Assert.Null((await store.GetAsync(missingClassification.Id))!.ReviewedAt);
    }

    private static async Task<Decision> AddWithSeverityAsync(
        InMemoryDecisionStore store,
        InMemoryClassificationStore classifications,
        DecisionOutcome outcome,
        int severity)
    {
        var classification = new Classification
        {
            Id = ViegardId.New(),
            SubjectKind = ClassificationSubjectKind.Incident,
            SubjectId = ViegardId.New(),
            ClassifierId = "test-classifier",
            Category = "scanner",
            Confidence = 0.5,
            Severity = severity,
            Reasons = ["test"],
            RecommendedAction = "temp-ban-ip",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await classifications.AddAsync(classification);
        var decision = Decision(outcome) with { ClassificationId = classification.Id };
        await store.AddAsync(decision);
        return decision;
    }

    private static Decision Decision(DecisionOutcome outcome) => new()
    {
        Id = ViegardId.New(),
        ClassificationId = ViegardId.New(),
        PolicyId = "test-policy",
        PolicyVersion = "1",
        Outcome = outcome,
        Rationale = "test decision",
        Guardrails = [],
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
