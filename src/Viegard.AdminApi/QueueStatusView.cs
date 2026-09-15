using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.AdminApi;

/// <summary>
/// Folds raw queue telemetry into the operator view.  Queues are global, so
/// each queue is represented by its freshest report across instances; each
/// instance remains represented by its newest telemetry heartbeat.
/// </summary>
public static class QueueStatusView
{
    public static SnapshotView Empty { get; } = new([], [], null);

    public sealed record SnapshotView(
        IReadOnlyList<QueueRow> Queues,
        IReadOnlyList<InstanceRow> Instances,
        TrafficLight? WorstLight);

    public sealed record QueueRow(QueueTelemetrySnapshot Snapshot, QueueHealthStatus Status);

    public sealed record InstanceRow(
        string InstanceId,
        IReadOnlyList<string> QueueNames,
        DateTimeOffset LastCapturedAt,
        TrafficLight Light,
        IReadOnlyList<string> Reasons)
    {
        public bool Green => Light == TrafficLight.Green;

        public string QueuesReported => QueueNames.Count switch
        {
            0 => "0",
            1 => QueueNames[0],
            _ => $"{QueueNames.Count}: {string.Join(", ", QueueNames)}",
        };
    }

    public static SnapshotView Build(
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        DateTimeOffset now,
        QueueHealthThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        if (snapshots.Count == 0)
        {
            return Empty;
        }

        var effectiveThresholds = thresholds ?? new QueueHealthThresholds();
        var evaluator = new QueueHealthEvaluator(effectiveThresholds);
        var queueRows = snapshots
            .GroupBy(snapshot => snapshot.QueueName, StringComparer.Ordinal)
            .Select(group => group.MaxBy(snapshot => snapshot.CapturedAt)!)
            .OrderBy(snapshot => snapshot.QueueName, StringComparer.Ordinal)
            .Select(snapshot => new QueueRow(snapshot, evaluator.Evaluate(snapshot, now)))
            .ToList();

        var instanceRows = snapshots
            .GroupBy(snapshot => snapshot.InstanceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => BuildInstanceRow(group, now, effectiveThresholds))
            .ToList();

        var worst = queueRows
            .Select(row => row.Status.Light)
            .Concat(instanceRows.Select(row => row.Light))
            .Max();

        return new SnapshotView(queueRows, instanceRows, worst);
    }

    private static InstanceRow BuildInstanceRow(
        IGrouping<string, QueueTelemetrySnapshot> group,
        DateTimeOffset now,
        QueueHealthThresholds thresholds)
    {
        var lastCapturedAt = group.Max(snapshot => snapshot.CapturedAt);
        var queueNames = group
            .Select(snapshot => snapshot.QueueName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var telemetryAge = now - lastCapturedAt;
        if (telemetryAge > thresholds.TelemetryStaleAfter)
        {
            return new InstanceRow(
                group.Key,
                queueNames,
                lastCapturedAt,
                TrafficLight.Red,
                [QueueHealthEvaluator.StaleTelemetryReason(telemetryAge)]);
        }

        return new InstanceRow(
            group.Key,
            queueNames,
            lastCapturedAt,
            TrafficLight.Green,
            ["Instance telemetry is fresh."]);
    }
}
