using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.AdminApi;

public sealed record QueueStatusSortState(string Key, SortDirection Direction);

public static class QueueStatusSortKeys
{
    public const string QueueName = "queue";
    public const string QueueLight = "queue-light";
    public const string QueueDepth = "queue-depth";
    public const string QueueInFlight = "queue-in-flight";
    public const string QueueOldestAge = "queue-oldest-age";
    public const string QueueDeadLetters = "queue-dead-letters";
    public const string QueueTotals = "queue-totals";
    public const string QueueCaptured = "queue-captured";
    public const string InstanceId = "instance";
    public const string InstanceQueues = "instance-queues";
    public const string InstanceLastCaptured = "instance-last-captured";
    public const string InstanceLight = "instance-light";
    public const string InstanceVersion = "instance-version";
    public const string InstanceStarted = "instance-started";

    public static QueueStatusSortState? Parse(string? key, string? direction)
    {
        var normalizedKey = key?.Trim().ToLowerInvariant();
        if (!IsKnown(normalizedKey))
        {
            return null;
        }

        return ListSortParser.ParseDirection(direction) is { } parsedDirection
            ? new QueueStatusSortState(normalizedKey!, parsedDirection)
            : null;
    }

    private static bool IsKnown(string? key) => key switch
    {
        QueueName
            or QueueLight
            or QueueDepth
            or QueueInFlight
            or QueueOldestAge
            or QueueDeadLetters
            or QueueTotals
            or QueueCaptured
            or InstanceId
            or InstanceQueues
            or InstanceLastCaptured
            or InstanceLight
            or InstanceVersion
            or InstanceStarted => true,
        _ => false,
    };
}

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
        DateTimeOffset? LastCapturedAt,
        TrafficLight? Light,
        IReadOnlyList<string> Reasons,
        InstanceRegistration? Registration)
    {
        public bool Green => Light is null or TrafficLight.Green;

        public string VersionLabel
        {
            get
            {
                if (Registration is null)
                {
                    return "unknown";
                }

                if (!string.IsNullOrWhiteSpace(Registration.CommitSha))
                {
                    return Registration.CommitSha.Length >= 9
                        ? Registration.CommitSha[..9]
                        : Registration.CommitSha;
                }

                return string.IsNullOrWhiteSpace(Registration.Version)
                    ? "unknown"
                    : Registration.Version;
            }
        }

        public string VersionTitle => Registration?.Version ?? "No instance registry row has been reported.";

        public DateTimeOffset? StartedAt => Registration?.StartedAt;

        public string QueuesReported => QueueNames.Count switch
        {
            0 => "-",
            1 => QueueNames[0],
            _ => $"{QueueNames.Count}: {string.Join(", ", QueueNames)}",
        };
    }

    public static SnapshotView Build(
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        DateTimeOffset now,
        QueueHealthThresholds? thresholds = null) =>
        Build(snapshots, [], now, sort: null, thresholds);

    public static SnapshotView Build(
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        IReadOnlyList<InstanceRegistration> registrations,
        DateTimeOffset now,
        QueueStatusSortState? sort = null,
        QueueHealthThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(registrations);

        if (snapshots.Count == 0 && registrations.Count == 0)
        {
            return Empty;
        }

        var effectiveThresholds = thresholds ?? new QueueHealthThresholds();
        var evaluator = new QueueHealthEvaluator(effectiveThresholds);
        var queueRows = snapshots
            .GroupBy(snapshot => snapshot.QueueName, StringComparer.Ordinal)
            .Select(group => group.MaxBy(snapshot => snapshot.CapturedAt)!)
            .Select(snapshot => new QueueRow(snapshot, evaluator.Evaluate(snapshot, now)))
            .ToList();

        var registrationsByInstance = registrations
            .GroupBy(registration => registration.InstanceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.MaxBy(registration => registration.ReportedAt)!,
                StringComparer.Ordinal);
        var snapshotsByInstance = snapshots
            .GroupBy(snapshot => snapshot.InstanceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var instanceRows = snapshotsByInstance.Keys
            .Concat(registrationsByInstance.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(instanceId =>
            {
                snapshotsByInstance.TryGetValue(instanceId, out var instanceSnapshots);
                registrationsByInstance.TryGetValue(instanceId, out var registration);
                return BuildInstanceRow(instanceId, instanceSnapshots ?? [], registration, now, effectiveThresholds);
            })
            .ToList();

        var queueLights = queueRows.Select(row => row.Status.Light);
        var instanceLights = instanceRows
            .Where(row => row.Light is not null)
            .Select(row => row.Light!.Value);
        var lights = queueLights.Concat(instanceLights).ToList();
        var worst = lights.Count == 0 ? (TrafficLight?)null : lights.Max();

        return new SnapshotView(SortQueues(queueRows, sort), SortInstances(instanceRows, sort), worst);
    }

    private static InstanceRow BuildInstanceRow(
        string instanceId,
        IReadOnlyList<QueueTelemetrySnapshot> snapshots,
        InstanceRegistration? registration,
        DateTimeOffset now,
        QueueHealthThresholds thresholds)
    {
        if (snapshots.Count == 0)
        {
            return new InstanceRow(instanceId, [], LastCapturedAt: null, Light: null, [], registration);
        }

        var lastCapturedAt = snapshots.Max(snapshot => snapshot.CapturedAt);
        var queueNames = snapshots
            .Select(snapshot => snapshot.QueueName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var telemetryAge = now - lastCapturedAt;
        if (telemetryAge > thresholds.TelemetryStaleAfter)
        {
            return new InstanceRow(
                instanceId,
                queueNames,
                lastCapturedAt,
                TrafficLight.Red,
                [QueueHealthEvaluator.StaleTelemetryReason(telemetryAge)],
                registration);
        }

        return new InstanceRow(
            instanceId,
            queueNames,
            lastCapturedAt,
            TrafficLight.Green,
            ["Instance telemetry is fresh."],
            registration);
    }

    private static IReadOnlyList<QueueRow> SortQueues(
        IReadOnlyList<QueueRow> rows,
        QueueStatusSortState? sort)
    {
        if (sort is null || !sort.Key.StartsWith("queue", StringComparison.Ordinal))
        {
            return rows.OrderBy(row => row.Snapshot.QueueName, StringComparer.Ordinal).ToList();
        }

        var ordered = sort.Key switch
        {
            QueueStatusSortKeys.QueueLight => Order(rows, row => row.Status.Light, sort.Direction),
            QueueStatusSortKeys.QueueDepth => Order(rows, row => row.Snapshot.Depth, sort.Direction),
            QueueStatusSortKeys.QueueInFlight => Order(rows, row => row.Snapshot.InFlight, sort.Direction),
            QueueStatusSortKeys.QueueOldestAge => Order(
                rows,
                row => row.Snapshot.OldestPendingEnqueuedAt is { } oldest
                    ? row.Snapshot.CapturedAt - oldest
                    : TimeSpan.Zero,
                sort.Direction),
            QueueStatusSortKeys.QueueDeadLetters => Order(rows, row => row.Snapshot.DeadLetterCount, sort.Direction),
            QueueStatusSortKeys.QueueTotals => Order(rows, row => row.Snapshot.TotalEnqueued, sort.Direction),
            QueueStatusSortKeys.QueueCaptured => Order(rows, row => row.Snapshot.CapturedAt, sort.Direction),
            _ => Order(rows, row => row.Snapshot.QueueName, sort.Direction, StringComparer.Ordinal),
        };

        return ordered.ThenBy(row => row.Snapshot.QueueName, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<InstanceRow> SortInstances(
        IReadOnlyList<InstanceRow> rows,
        QueueStatusSortState? sort)
    {
        if (sort is null || !sort.Key.StartsWith("instance", StringComparison.Ordinal))
        {
            return rows.OrderBy(row => row.InstanceId, StringComparer.Ordinal).ToList();
        }

        var ordered = sort.Key switch
        {
            QueueStatusSortKeys.InstanceQueues => Order(rows, row => row.QueueNames.Count, sort.Direction),
            QueueStatusSortKeys.InstanceLastCaptured => Order(rows, row => row.LastCapturedAt, sort.Direction),
            QueueStatusSortKeys.InstanceLight => Order(rows, row => row.Light, sort.Direction),
            QueueStatusSortKeys.InstanceVersion => Order(rows, row => row.VersionLabel, sort.Direction, StringComparer.Ordinal),
            QueueStatusSortKeys.InstanceStarted => Order(rows, row => row.StartedAt, sort.Direction),
            _ => Order(rows, row => row.InstanceId, sort.Direction, StringComparer.Ordinal),
        };

        return ordered.ThenBy(row => row.InstanceId, StringComparer.Ordinal).ToList();
    }

    private static IOrderedEnumerable<T> Order<T, TKey>(
        IEnumerable<T> rows,
        Func<T, TKey> keySelector,
        SortDirection direction,
        IComparer<TKey>? comparer = null) =>
        direction == SortDirection.Asc
            ? rows.OrderBy(keySelector, comparer ?? Comparer<TKey>.Default)
            : rows.OrderByDescending(keySelector, comparer ?? Comparer<TKey>.Default);
}
