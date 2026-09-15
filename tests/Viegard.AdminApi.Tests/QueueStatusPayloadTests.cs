using Viegard.AdminApi;
using Viegard.Application.Retention;
using Viegard.Domain.Health;

namespace Viegard.AdminApi.Tests;

public sealed class QueueStatusPayloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);

    private static QueueTelemetrySnapshot Snapshot(
        string queue = "events",
        int depth = 1,
        DateTimeOffset? oldest = null,
        DateTimeOffset? captured = null) => new()
    {
        InstanceId = "pipeline-1",
        QueueName = queue,
        Depth = depth,
        InFlight = 2,
        OldestPendingEnqueuedAt = oldest,
        TotalEnqueued = 100,
        TotalCompleted = 90,
        TotalAbandoned = 3,
        DeadLetterCount = 0,
        CapturedAt = captured ?? Now.AddSeconds(-5),
    };

    [Fact]
    public void Build_produces_display_ready_rows_keyed_by_instance_and_queue()
    {
        var payload = QueueStatusPayload.Build(
            [Snapshot(oldest: Now.AddSeconds(-10))],
            Now,
            value => $"F:{value:HH:mm:ss}",
            retentionSettings: null);

        var row = Assert.Single(payload.Rows);
        Assert.Equal("pipeline-1|events", row.Key);
        Assert.Equal("Green", row.Light);
        Assert.Equal("badge badge-green", row.LightCss);
        Assert.True(row.Green);
        Assert.Equal(1, row.Depth);
        Assert.Equal(2, row.InFlight);
        Assert.Equal("0:00:10", row.OldestAge);
        Assert.Equal("100 / 90 / 3", row.Totals);
        Assert.Equal("F:19:59:55", row.Captured);
        Assert.Equal("Queue is healthy.", row.Reasons);
        Assert.Equal("status-dot-green", payload.Css);
        Assert.Equal("Healthy", payload.Label);
    }

    [Fact]
    public void Build_reports_reasons_for_unhealthy_queues()
    {
        // A snapshot captured far in the past is stale, which is a red light.
        var payload = QueueStatusPayload.Build(
            [Snapshot(captured: Now.AddHours(-2))],
            Now,
            value => value.ToString("O"),
            retentionSettings: null);

        var row = Assert.Single(payload.Rows);
        Assert.False(row.Green);
        Assert.NotEqual("badge badge-green", row.LightCss);
        Assert.False(string.IsNullOrWhiteSpace(row.Reasons));
    }

    [Fact]
    public void Build_reports_retention_last_cycle_when_present_and_never_when_absent()
    {
        var none = QueueStatusPayload.Build([], Now, value => value.ToString("O"), retentionSettings: null);
        Assert.Equal("Never", none.Retention.LastCycle);
        Assert.Equal(0, none.Retention.LastRowsRemoved);
        Assert.Equal("status-dot-muted", none.Css);

        var settings = new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            Version = 1,
            UpdatedAt = Now,
            UpdatedBy = "test",
            LastCycleAt = Now.AddMinutes(-10),
        };
        var payload = QueueStatusPayload.Build([], Now, value => $"F:{value:HH:mm}", settings);
        Assert.Equal("F:19:50", payload.Retention.LastCycle);
    }
}
