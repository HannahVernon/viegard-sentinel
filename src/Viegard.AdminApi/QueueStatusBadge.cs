using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.AdminApi;

/// <summary>
/// Single place that folds queue dashboard telemetry into one header badge:
/// the worst freshest-per-queue status or per-instance staleness light wins.
/// Used by the layout at render time and by the /status/queues polling endpoint.
/// </summary>
public static class QueueStatusBadge
{
    public static (string Css, string Label) Summarize(
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        DateTimeOffset now) =>
        ToBadge(QueueStatusView.Build(snapshots, now).WorstLight);

    public static (string Css, string Label) ToBadge(TrafficLight? light) =>
        light switch
        {
            null => ("status-dot-muted", "No data"),
            TrafficLight.Green => ("status-dot-green", "Healthy"),
            TrafficLight.Amber => ("status-dot-amber", "Degraded"),
            TrafficLight.Red => ("status-dot-red", "Alert"),
            _ => ("status-dot-red", "Alert"),
        };
}
