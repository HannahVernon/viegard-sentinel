using Viegard.AdminApi;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain.Health;

namespace Viegard.AdminApi.Tests;

public sealed class QueueStatusPayloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);

    private static QueueTelemetrySnapshot Snapshot(
        string instance = "pipeline-1",
        string queue = "events",
        int depth = 1,
        int inFlight = 2,
        DateTimeOffset? oldest = null,
        DateTimeOffset? captured = null,
        int deadLetters = 0,
        long totalEnqueued = 100,
        long totalCompleted = 90,
        long totalAbandoned = 3) => new()
    {
        InstanceId = instance,
        QueueName = queue,
        Depth = depth,
        InFlight = inFlight,
        OldestPendingEnqueuedAt = oldest,
        TotalEnqueued = totalEnqueued,
        TotalCompleted = totalCompleted,
        TotalAbandoned = totalAbandoned,
        DeadLetterCount = deadLetters,
        CapturedAt = captured ?? Now.AddSeconds(-5),
    };

    [Fact]
    public void Build_deduplicates_queues_to_freshest_snapshot_and_reports_instance()
    {
        var payload = QueueStatusPayload.Build(
            [
                Snapshot(
                    instance: "pipeline-1",
                    depth: 7,
                    inFlight: 5,
                    oldest: Now.AddSeconds(-30),
                    captured: Now.AddSeconds(-40),
                    totalEnqueued: 700,
                    totalCompleted: 690,
                    totalAbandoned: 1),
                Snapshot(
                    instance: "satellite-1",
                    depth: 2,
                    inFlight: 1,
                    oldest: Now.AddSeconds(-10),
                    captured: Now.AddSeconds(-5),
                    deadLetters: 1,
                    totalEnqueued: 200,
                    totalCompleted: 190,
                    totalAbandoned: 4),
            ],
            [],
            Now,
            value => $"F:{value:HH:mm:ss}",
            retentionSettings: null);

        var row = Assert.Single(payload.Rows);
        Assert.Equal("events", row.Key);
        Assert.Equal("Amber", row.Light);
        Assert.Equal("badge badge-amber", row.LightCss);
        Assert.False(row.Green);
        Assert.Equal(2, row.Depth);
        Assert.Equal(1, row.InFlight);
        Assert.Equal("10 s", row.OldestAge);
        Assert.Equal(1, row.DeadLetters);
        Assert.Equal("200 / 190 / 4", row.Totals);
        Assert.Equal("F:19:59:55 by satellite-1", row.Captured);

        Assert.Equal(2, payload.Instances.Count);
        Assert.Contains(payload.Instances, instance => instance.Key == "pipeline-1");
        Assert.Contains(payload.Instances, instance => instance.Key == "satellite-1");
        Assert.Equal("status-dot-amber", payload.Css);
        Assert.Equal("Degraded", payload.Label);
    }

    [Fact]
    public void Build_keeps_stale_instance_red_when_another_instance_reports_queue_fresh()
    {
        var payload = QueueStatusPayload.Build(
            [
                Snapshot(instance: "pipeline-1", depth: 0, captured: Now.AddSeconds(-5)),
                Snapshot(instance: "satellite-1", depth: 0, captured: Now.AddSeconds(-61)),
            ],
            [],
            Now,
            value => $"F:{value:HH:mm:ss}",
            retentionSettings: null);

        var queue = Assert.Single(payload.Rows);
        Assert.Equal("events", queue.Key);
        Assert.Equal("Green", queue.Light);
        Assert.True(queue.Green);
        Assert.Equal("F:19:59:55 by pipeline-1", queue.Captured);

        var freshInstance = Assert.Single(payload.Instances, instance => instance.Key == "pipeline-1");
        Assert.Equal("Green", freshInstance.Light);
        Assert.Equal("badge badge-green", freshInstance.LightCss);
        Assert.True(freshInstance.Green);

        var staleInstance = Assert.Single(payload.Instances, instance => instance.Key == "satellite-1");
        Assert.Equal("Red", staleInstance.Light);
        Assert.Equal("badge badge-red", staleInstance.LightCss);
        Assert.False(staleInstance.Green);
        Assert.Contains("Telemetry is stale", staleInstance.Reasons);

        Assert.Equal("status-dot-red", payload.Css);
        Assert.Equal("Alert", payload.Label);
    }

    [Fact]
    public void Build_payload_shape_uses_queue_keys_and_instances_array()
    {
        var payload = QueueStatusPayload.Build(
            [
                Snapshot(instance: "pipeline-1", queue: "actions"),
                Snapshot(instance: "pipeline-1", queue: "events"),
            ],
            [],
            Now,
            value => $"F:{value:HH:mm:ss}",
            retentionSettings: null);

        Assert.Equal(["actions", "events"], payload.Rows.Select(row => row.Key).ToArray());
        var instance = Assert.Single(payload.Instances);
        Assert.Equal("pipeline-1", instance.Key);
        Assert.Equal("2: actions, events", instance.QueuesReported);
        Assert.Equal("F:19:59:55", instance.LastCaptured);
        Assert.Equal("Green", instance.Light);
    }

    [Fact]
    public void Build_unions_registry_only_instances_and_formats_version_columns()
    {
        const string Sha = "f2f974293d366eb28e9738b1050397592b4721b4";
        var payload = QueueStatusPayload.Build(
            [Snapshot(instance: "pipeline-1", queue: "events", captured: Now.AddSeconds(-5))],
            [
                new InstanceRegistration
                {
                    InstanceId = "admin-mailhost",
                    Version = "1.0.0+" + Sha,
                    CommitSha = Sha,
                    Roles = "admin",
                    HostName = "mailhost",
                    StartedAt = Now.AddHours(-3),
                    ReportedAt = Now.AddSeconds(-1),
                },
            ],
            Now,
            value => $"F:{value:HH:mm:ss}",
            retentionSettings: null);

        var admin = Assert.Single(payload.Instances, instance => instance.Key == "admin-mailhost");
        Assert.Equal("-", admin.QueuesReported);
        Assert.Equal("-", admin.LastCaptured);
        Assert.Equal("-", admin.Light);
        Assert.Equal("badge", admin.LightCss);
        Assert.Equal("f2f974293", admin.Version);
        Assert.Equal("1.0.0+" + Sha, admin.VersionTitle);
        Assert.Equal("3 h 0 m", admin.StartedAge);

        var pipeline = Assert.Single(payload.Instances, instance => instance.Key == "pipeline-1");
        Assert.Equal("unknown", pipeline.Version);
        Assert.Equal("No instance registry row has been reported.", pipeline.VersionTitle);
    }

    [Fact]
    public void Build_sorts_instances_by_version_and_started_columns()
    {
        var older = Registration(
            "instance-b",
            "1.0.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Now.AddHours(-3),
            Now.AddSeconds(-2));
        var newer = Registration(
            "instance-a",
            "1.0.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Now.AddHours(-1),
            Now.AddSeconds(-1));

        var versionSorted = QueueStatusView.Build(
            [],
            [older, newer],
            Now,
            new QueueStatusSortState(QueueStatusSortKeys.InstanceVersion, SortDirection.Asc));
        Assert.Equal(["instance-a", "instance-b"], versionSorted.Instances.Select(row => row.InstanceId).ToArray());

        var startedSorted = QueueStatusView.Build(
            [],
            [older, newer],
            Now,
            new QueueStatusSortState(QueueStatusSortKeys.InstanceStarted, SortDirection.Desc));
        Assert.Equal(["instance-a", "instance-b"], startedSorted.Instances.Select(row => row.InstanceId).ToArray());
    }

    [Fact]
    public void Summarize_folds_freshest_queues_and_instance_staleness()
    {
        var status = QueueStatusBadge.Summarize(
            [
                Snapshot(instance: "pipeline-1", depth: 0, captured: Now.AddSeconds(-5)),
                Snapshot(instance: "satellite-1", depth: 0, captured: Now.AddSeconds(-61)),
            ],
            Now);

        Assert.Equal(("status-dot-red", "Alert"), status);

        var amber = QueueStatusBadge.Summarize(
            [
                Snapshot(instance: "pipeline-1", depth: 150, captured: Now.AddSeconds(-5)),
                Snapshot(instance: "satellite-1", depth: 0, captured: Now.AddSeconds(-40)),
            ],
            Now);

        Assert.Equal(("status-dot-amber", "Degraded"), amber);
    }

    [Fact]
    public void Build_reports_retention_last_cycle_when_present_and_never_when_absent()
    {
        var none = QueueStatusPayload.Build([], [], Now, value => value.ToString("O"), retentionSettings: null);
        Assert.Equal("Never", none.Retention.LastCycle);
        Assert.Equal(0, none.Retention.LastRowsRemoved);
        Assert.Equal("status-dot-muted", none.Css);
        Assert.Empty(none.Instances);

        var settings = new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            Version = 1,
            UpdatedAt = Now,
            UpdatedBy = "test",
            LastCycleAt = Now.AddMinutes(-10),
            LastCycleCountsJson = RetentionSettings.SerializeCounts(new Dictionary<RetentionTarget, long>
            {
                [RetentionTarget.Events] = 4,
                [RetentionTarget.AuditRecords] = 2,
            }),
        };
        var payload = QueueStatusPayload.Build([], [], Now, value => $"F:{value:HH:mm}", settings);
        Assert.Equal("F:19:50", payload.Retention.LastCycle);
        Assert.Equal(6, payload.Retention.LastRowsRemoved);
    }

    private static InstanceRegistration Registration(
        string instanceId,
        string version,
        DateTimeOffset startedAt,
        DateTimeOffset reportedAt) => new()
    {
        InstanceId = instanceId,
        Version = version,
        CommitSha = BuildVersion.Parse(version).CommitSha,
        Roles = "admin",
        HostName = "host.example.com",
        StartedAt = startedAt,
        ReportedAt = reportedAt,
    };
}
