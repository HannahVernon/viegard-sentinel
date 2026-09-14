using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.AdminApi;

/// <summary>
/// Single place that folds all queue telemetry snapshots into one header
/// badge: the worst D-0012 traffic light wins.  Used by the layout at
/// render time and by the /status/queues polling endpoint.
/// </summary>
public static class QueueStatusBadge
{
    public static (string Css, string Label) Summarize(
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        DateTimeOffset now)
    {
        if (snapshots.Count == 0)
        {
            return ("status-dot-muted", "No data");
        }

        var evaluator = new QueueHealthEvaluator(new QueueHealthThresholds());
        var worst = snapshots.Max(s => evaluator.Evaluate(s, now).Light);
        return worst switch
        {
            TrafficLight.Green => ("status-dot-green", "Healthy"),
            TrafficLight.Amber => ("status-dot-amber", "Degraded"),
            _ => ("status-dot-red", "Alert"),
        };
    }
}
