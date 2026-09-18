using Viegard.AdminApi.Components;
using Viegard.Domain;
using Viegard.Domain.Actions;

namespace Viegard.AdminApi.Tests;

public sealed class BanStatusPayloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

    private static string Format(DateTimeOffset value) => value.ToString("u");

    [Fact]
    public void Build_maps_active_bans_display_ready()
    {
        var decisionId = Guid.Parse("0123456789abcdef0123456789abcdef");
        var ban = new ActiveBan
        {
            Id = ViegardId.New(),
            Ip = "203.0.113.10",
            CreatedAt = Now.AddHours(-1),
            ExpiresAt = Now.AddHours(23),
            DecisionId = decisionId,
            ActionId = ViegardId.New(),
        };

        var payload = BanStatusPayload.Build([ban], [], Now, Format);

        var row = Assert.Single(payload.ActiveBans);
        Assert.Equal("203.0.113.10", row.Ip);
        Assert.Equal(AdminText.Age(TimeSpan.FromHours(23)), row.ExpiresIn);
        Assert.Equal(Format(ban.CreatedAt), row.Created);
        Assert.Equal("0123456789abcdef0123456789abcdef", row.DecisionId);
        Assert.Equal(AdminText.ShortId(decisionId), row.DecisionShort);
        Assert.Empty(payload.RecentActions);
    }

    [Fact]
    public void Build_maps_actions_display_ready_with_status_css()
    {
        var completed = Now.AddSeconds(-30);
        var succeeded = Action(ActionStatus.Succeeded, completedAt: completed);
        var pending = Action(ActionStatus.Pending, completedAt: null);

        var payload = BanStatusPayload.Build([], [succeeded, pending], Now, Format);

        Assert.Equal(2, payload.RecentActions.Count);
        var first = payload.RecentActions[0];
        Assert.Equal(succeeded.Id.ToString("N"), first.Key);
        Assert.Equal("ban-ip", first.Operation);
        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("badge badge-green", first.StatusCss);
        Assert.Equal(Format(succeeded.RequestedAt), first.Requested);
        Assert.Equal(Format(completed), first.Completed);
        Assert.Equal(AdminText.ShortId(succeeded.DecisionId), first.DecisionShort);

        var second = payload.RecentActions[1];
        Assert.Equal("badge badge-amber", second.StatusCss);
        Assert.Equal("-", second.Completed);
    }

    [Theory]
    [InlineData(ActionStatus.Succeeded, "badge badge-green")]
    [InlineData(ActionStatus.DryRun, "badge badge-blue")]
    [InlineData(ActionStatus.Pending, "badge badge-amber")]
    [InlineData(ActionStatus.Failed, "badge badge-red")]
    [InlineData(ActionStatus.RolledBack, "badge")]
    public void StatusCss_stays_inside_client_whitelist(ActionStatus status, string expected) =>
        Assert.Equal(expected, BanStatusPayload.StatusCss(status));

    private static ActionRecord Action(ActionStatus status, DateTimeOffset? completedAt) => new()
    {
        Id = ViegardId.New(),
        DecisionId = ViegardId.New(),
        ProviderId = "mikrotik",
        OperationId = "ban-ip",
        ParametersJson = """{"ip":"203.0.113.10","timeout":"1d"}""",
        Status = status,
        RequestedAt = Now.AddMinutes(-1),
        CompletedAt = completedAt,
    };
}
