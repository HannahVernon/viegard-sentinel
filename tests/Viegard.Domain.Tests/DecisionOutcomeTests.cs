using Viegard.Domain.Decisions;

namespace Viegard.Domain.Tests;

public sealed class DecisionOutcomeTests
{
    [Fact]
    public void Persisted_integer_values_are_stable()
    {
        Assert.Equal(0, (int)DecisionOutcome.ActionAuthorized);
        Assert.Equal(1, (int)DecisionOutcome.RecordOnly);
        Assert.Equal(2, (int)DecisionOutcome.RequireApproval);
        Assert.Equal(3, (int)DecisionOutcome.DryRun);
    }
}
