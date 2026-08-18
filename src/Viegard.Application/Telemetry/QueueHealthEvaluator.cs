using Viegard.Domain.Health;

namespace Viegard.Application.Telemetry;

public enum TrafficLight
{
    Green,
    Amber,
    Red,
}

/// <summary>
/// Thresholds for deriving queue traffic-light status.  All values are
/// configuration; the defaults below are conservative placeholders pending
/// Hannah's approval (see TODO.md).
/// </summary>
public sealed record QueueHealthThresholds
{
    public TimeSpan AmberOldestPendingAge { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RedOldestPendingAge { get; init; } = TimeSpan.FromMinutes(5);

    public int AmberDepth { get; init; } = 100;

    public int RedDepth { get; init; } = 1_000;

    /// <summary>Telemetry older than this is itself red: nothing is publishing.</summary>
    public TimeSpan TelemetryStaleAfter { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Traffic-light status for one queue, with the reasons that produced it.</summary>
public sealed record QueueHealthStatus
{
    public required string InstanceId { get; init; }

    public required string QueueName { get; init; }

    public required TrafficLight Light { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// Derives green/amber/red status from a telemetry snapshot (D-0012).  The
/// primary timeliness signal is the age of the oldest pending message;
/// snapshot staleness is checked first because a dead publisher makes every
/// other number meaningless.
/// </summary>
public sealed class QueueHealthEvaluator(QueueHealthThresholds thresholds)
{
    public QueueHealthStatus Evaluate(QueueTelemetrySnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var reasons = new List<string>();
        var light = TrafficLight.Green;

        var telemetryAge = now - snapshot.CapturedAt;
        if (telemetryAge > thresholds.TelemetryStaleAfter)
        {
            return new QueueHealthStatus
            {
                InstanceId = snapshot.InstanceId,
                QueueName = snapshot.QueueName,
                Light = TrafficLight.Red,
                Reasons = [$"Telemetry is stale ({telemetryAge:g} old): the publisher may be down."],
            };
        }

        if (snapshot.OldestPendingEnqueuedAt is { } oldest)
        {
            var pendingAge = now - oldest;
            if (pendingAge >= thresholds.RedOldestPendingAge)
            {
                light = TrafficLight.Red;
                reasons.Add($"Oldest pending message has waited {pendingAge:g} (red threshold {thresholds.RedOldestPendingAge:g}).");
            }
            else if (pendingAge >= thresholds.AmberOldestPendingAge)
            {
                light = TrafficLight.Amber;
                reasons.Add($"Oldest pending message has waited {pendingAge:g} (amber threshold {thresholds.AmberOldestPendingAge:g}).");
            }
        }

        if (snapshot.Depth >= thresholds.RedDepth)
        {
            light = TrafficLight.Red;
            reasons.Add($"Depth {snapshot.Depth} is at or above the red threshold {thresholds.RedDepth}.");
        }
        else if (snapshot.Depth >= thresholds.AmberDepth && light == TrafficLight.Green)
        {
            light = TrafficLight.Amber;
            reasons.Add($"Depth {snapshot.Depth} is at or above the amber threshold {thresholds.AmberDepth}.");
        }

        if (snapshot.DeadLetterCount > 0 && light == TrafficLight.Green)
        {
            light = TrafficLight.Amber;
            reasons.Add($"{snapshot.DeadLetterCount} dead-lettered message(s) require attention.");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Queue is healthy.");
        }

        return new QueueHealthStatus
        {
            InstanceId = snapshot.InstanceId,
            QueueName = snapshot.QueueName,
            Light = light,
            Reasons = reasons,
        };
    }
}
