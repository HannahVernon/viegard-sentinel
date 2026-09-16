using Viegard.AdminApi;
using Viegard.Application.Configuration;

namespace Viegard.AdminApi.Tests;

public sealed class HostUpgradeStatusPayloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 16, 0, 0, TimeSpan.Zero);

    private static HostUpgradeCommand Command(
        HostUpgradeCommandStatus status = HostUpgradeCommandStatus.Pending,
        DateTimeOffset? requestedAt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null,
        string? detail = null) => new()
    {
        Id = Guid.Parse("0123456789abcdef0123456789abcdef"),
        Target = "vm",
        Status = status,
        RequestedAt = requestedAt ?? Now.AddMinutes(-1),
        RequestedBy = "admin",
        StartedAt = startedAt,
        FinishedAt = finishedAt,
        Detail = detail,
    };

    private static string Format(DateTimeOffset value) => value.ToString("u");

    [Fact]
    public void Build_maps_command_fields_display_ready()
    {
        var started = Now.AddSeconds(-50);
        var finished = Now.AddSeconds(-10);
        var payload = HostUpgradeStatusPayload.Build(
            [Command(HostUpgradeCommandStatus.Succeeded, startedAt: started, finishedAt: finished, detail: "done")],
            Now,
            Format);

        var row = Assert.Single(payload.Commands);
        Assert.Equal("0123456789abcdef0123456789abcdef", row.Key);
        Assert.Equal("Succeeded", row.Status);
        Assert.Equal("badge badge-green", row.StatusCss);
        Assert.Equal(Format(started), row.Started);
        Assert.Equal(Format(finished), row.Finished);
        Assert.Equal("done", row.Detail);
        Assert.False(row.HasLongDetail);
        Assert.False(payload.StalePending);
    }

    [Fact]
    public void Build_leaves_unset_timestamps_and_detail_empty()
    {
        var payload = HostUpgradeStatusPayload.Build(
            [Command(HostUpgradeCommandStatus.Running, startedAt: Now.AddSeconds(-5))],
            Now,
            Format);

        var row = Assert.Single(payload.Commands);
        Assert.Equal("badge badge-blue", row.StatusCss);
        Assert.NotEqual(string.Empty, row.Started);
        Assert.Equal(string.Empty, row.Finished);
        Assert.Equal(string.Empty, row.Detail);
        Assert.Equal(string.Empty, row.FullDetail);
        Assert.False(row.HasLongDetail);
    }

    [Fact]
    public void Build_flags_long_detail_and_flattens_the_summary_to_one_line()
    {
        var detail = string.Join("\n", Enumerable.Repeat("0123456789", 20));
        var payload = HostUpgradeStatusPayload.Build(
            [Command(HostUpgradeCommandStatus.Failed, detail: detail)],
            Now,
            Format);

        var row = Assert.Single(payload.Commands);
        Assert.Equal("badge badge-red", row.StatusCss);
        Assert.True(row.HasLongDetail);
        Assert.DoesNotContain('\n', row.Detail);
        Assert.EndsWith("... [truncated]", row.Detail);
        Assert.Contains("0123456789", row.FullDetail);
    }

    [Fact]
    public void Build_reports_stale_pending_commands()
    {
        var stale = Command(
            HostUpgradeCommandStatus.Pending,
            requestedAt: Now - HostUpgradeCommandPolicy.PendingStaleAfter - TimeSpan.FromSeconds(1));

        var payload = HostUpgradeStatusPayload.Build([stale], Now, Format);

        Assert.True(payload.StalePending);
    }

    [Theory]
    [InlineData(HostUpgradeCommandStatus.Pending, "badge badge-amber")]
    [InlineData(HostUpgradeCommandStatus.Running, "badge badge-blue")]
    [InlineData(HostUpgradeCommandStatus.Succeeded, "badge badge-green")]
    [InlineData(HostUpgradeCommandStatus.Failed, "badge badge-red")]
    [InlineData(HostUpgradeCommandStatus.Superseded, "badge")]
    public void StatusCss_maps_every_status_to_a_known_badge(HostUpgradeCommandStatus status, string expected)
    {
        Assert.Equal(expected, HostUpgradeStatusPayload.StatusCss(status));
    }
}
