using Viegard.AdminApi.Components;
using Viegard.Domain.Decisions;

namespace Viegard.AdminApi.Tests;

public sealed class AdminTextTests
{
    [Fact]
    public void Decision_outcome_labels_are_operator_facing()
    {
        Assert.Equal("Action authorized", AdminText.DecisionOutcomeLabel(DecisionOutcome.ActionAuthorized));
        Assert.Equal("Record only", AdminText.DecisionOutcomeLabel(DecisionOutcome.RecordOnly));
        Assert.Equal("Require approval", AdminText.DecisionOutcomeLabel(DecisionOutcome.RequireApproval));
        Assert.Equal("Dry run", AdminText.DecisionOutcomeLabel(DecisionOutcome.DryRun));
    }

    [Fact]
    public void Decision_outcome_badges_match_operator_semantics()
    {
        Assert.Equal("badge badge-green", AdminText.OutcomeCss(DecisionOutcome.ActionAuthorized));
        Assert.Equal("badge", AdminText.OutcomeCss(DecisionOutcome.RecordOnly));
        Assert.Equal("badge badge-amber", AdminText.OutcomeCss(DecisionOutcome.RequireApproval));
        Assert.Equal("badge badge-blue", AdminText.OutcomeCss(DecisionOutcome.DryRun));
    }
}
