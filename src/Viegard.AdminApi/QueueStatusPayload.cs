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

    public sealed record RetentionPayload(string LastCycle, long LastRowsRemoved);

    public sealed record Payload(
        string Css,
        string Label,
        IReadOnlyList<QueueRowPayload> Rows,
        RetentionPayload Retention);

    public static Payload Build(
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        DateTimeOffset now,
        Func<DateTimeOffset, string> format,
        RetentionSettings? retentionSettings)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(format);

        var (css, label) = QueueStatusBadge.Summarize(snapshots, now);
        var evaluator = new QueueHealthEvaluator(new QueueHealthThresholds());
        var rows = snapshots
            .Select(snapshot =>
            {
                var status = evaluator.Evaluate(snapshot, now);
                return new QueueRowPayload(
                    Key: $"{snapshot.InstanceId}|{snapshot.QueueName}",
                    Light: status.Light.ToString(),
                    LightCss: AdminText.LightCss(status.Light),
                    Depth: snapshot.Depth,
                    InFlight: snapshot.InFlight,
                    OldestAge: snapshot.OldestPendingEnqueuedAt is { } oldest
                        ? (now - oldest).ToString("g")
                        : string.Empty,
                    DeadLetters: snapshot.DeadLetterCount,
                    Totals: $"{snapshot.TotalEnqueued} / {snapshot.TotalCompleted} / {snapshot.TotalAbandoned}",
                    Captured: format(snapshot.CapturedAt),
                    Reasons: string.Join(" ", status.Reasons),
                    Green: status.Light == TrafficLight.Green);
            })
            .ToList();

        var retention = new RetentionPayload(
            LastCycle: retentionSettings?.LastCycleAt is { } lastCycle ? format(lastCycle) : "Never",
            LastRowsRemoved: retentionSettings?.LastCycleCounts().Values.Sum() ?? 0);

        return new Payload(css, label, rows, retention);
    }
}
