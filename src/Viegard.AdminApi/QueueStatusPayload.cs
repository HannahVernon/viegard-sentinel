using Viegard.AdminApi.Components;
using Viegard.Application.Retention;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.AdminApi;

/// <summary>
/// Builds the display-ready JSON payload for the /status/queues polling
/// endpoint.  All strings are formatted server-side (user time zone via the
/// supplied formatter) so the client script only swaps text content.
/// </summary>
public static class QueueStatusPayload
{
    public sealed record QueueRowPayload(
        string Key,
        string Light,
        string LightCss,
        int Depth,
        int InFlight,
        string OldestAge,
        int DeadLetters,
        string Totals,
        string Captured,
        string Reasons,
        bool Green);

    public sealed record InstanceRowPayload(
        string Key,
        string LastCaptured,
        string Light,
        string LightCss,
        string QueuesReported,
        string Reasons,
        bool Green);

    public sealed record RetentionPayload(string LastCycle, long LastRowsRemoved);

    public sealed record Payload(
        string Css,
        string Label,
        IReadOnlyList<QueueRowPayload> Rows,
        IReadOnlyList<InstanceRowPayload> Instances,
        RetentionPayload Retention);

    public static Payload Build(
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        DateTimeOffset now,
        Func<DateTimeOffset, string> format,
        RetentionSettings? retentionSettings)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(format);

        var view = QueueStatusView.Build(snapshots, now);
        var (css, label) = QueueStatusBadge.ToBadge(view.WorstLight);
        var rows = view.Queues
            .Select(row =>
            {
                var snapshot = row.Snapshot;
                var status = row.Status;
                return new QueueRowPayload(
                    Key: snapshot.QueueName,
                    Light: status.Light.ToString(),
                    LightCss: AdminText.LightCss(status.Light),
                    Depth: snapshot.Depth,
                    InFlight: snapshot.InFlight,
                    OldestAge: snapshot.OldestPendingEnqueuedAt is { } oldest
                        ? AdminText.Age(now - oldest)
                        : string.Empty,
                    DeadLetters: snapshot.DeadLetterCount,
                    Totals: $"{snapshot.TotalEnqueued} / {snapshot.TotalCompleted} / {snapshot.TotalAbandoned}",
                    Captured: $"{format(snapshot.CapturedAt)} by {snapshot.InstanceId}",
                    Reasons: string.Join(" ", status.Reasons),
                    Green: status.Light == TrafficLight.Green);
            })
            .ToList();

        var instances = view.Instances
            .Select(row => new InstanceRowPayload(
                Key: row.InstanceId,
                LastCaptured: format(row.LastCapturedAt),
                Light: row.Light.ToString(),
                LightCss: AdminText.LightCss(row.Light),
                QueuesReported: row.QueuesReported,
                Reasons: string.Join(" ", row.Reasons),
                Green: row.Green))
            .ToList();

        var retention = new RetentionPayload(
            LastCycle: retentionSettings?.LastCycleAt is { } lastCycle ? format(lastCycle) : "Never",
            LastRowsRemoved: retentionSettings?.LastCycleCounts().Values.Sum() ?? 0);

        return new Payload(css, label, rows, instances, retention);
    }
}
