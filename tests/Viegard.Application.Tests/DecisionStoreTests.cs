using Viegard.Domain;
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
