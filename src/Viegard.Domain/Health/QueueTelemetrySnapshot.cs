namespace Viegard.Domain.Health;

/// <summary>
/// A persisted point-in-time snapshot of one queue's statistics, published by
/// pipeline hosts and read by the admin service to derive traffic-light queue
/// health (D-0012).  Stale snapshots are themselves a red signal: they mean
/// nothing is publishing, even if the queue was empty at last report.
/// </summary>
public sealed record QueueTelemetrySnapshot
{
    public required string InstanceId { get; init; }

    public required string QueueName { get; init; }

    public required int Depth { get; init; }

    public required int InFlight { get; init; }

    public DateTimeOffset? OldestPendingEnqueuedAt { get; init; }

    public required long TotalEnqueued { get; init; }

    public required long TotalCompleted { get; init; }

    public required long TotalAbandoned { get; init; }

    public required int DeadLetterCount { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}
