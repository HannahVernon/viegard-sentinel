using System.Text.Json;
using Viegard.Actions.MikroTik;
using Viegard.AdminApi.Decisions;
using Viegard.Domain;
using Viegard.Domain.Actions;

namespace Viegard.AdminApi.Tests;

public sealed class BanActionSummaryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Summarize_reports_all_applied_counts()
    {
        var action = Action(Results(
            Result("gr1", "applied"),
            Result("gr2", "applied"),
            Result("gr3", "applied-without-call")));

        Assert.Equal("applied 3/3", BanActionSummary.Summarize(action));
    }

    [Fact]
    public void Summarize_reports_failed_router_names()
    {
        var action = Action(Results(
            Result("gr1", "applied"),
            Result("gr2", "failed")));

        Assert.Equal("failed gr2", BanActionSummary.Summarize(action));
    }

    [Fact]
    public void Summarize_reports_dry_run_counts()
    {
        var action = Action(Results(
            Result("gr1", "dry-run"),
            Result("gr2", "dry-run")));

        Assert.Equal("dry run 2/2", BanActionSummary.Summarize(action));
    }

    [Fact]
    public void Summarize_handles_unreadable_results()
    {
        var action = Action("{not json");

        Assert.Equal("Router results are not readable.", BanActionSummary.Summarize(action));
    }

    private static ActionRecord Action(string? resultsJson) => new()
    {
        Id = ViegardId.New(),
        DecisionId = ViegardId.New(),
        ProviderId = "mikrotik",
        OperationId = "ban-ip",
        Status = ActionStatus.Succeeded,
        ResultsJson = resultsJson,
        RequestedAt = DateTimeOffset.UtcNow,
    };

    private static string Results(params MikroTikRouterActionResult[] results) =>
        JsonSerializer.Serialize(results, Json);

    private static MikroTikRouterActionResult Result(string routerName, string status) => new()
    {
        RouterId = ViegardId.New(),
        RouterName = routerName,
        Attempts = 1,
        Status = status,
        Detail = status,
        LastAttemptAt = DateTimeOffset.UtcNow,
    };
}
