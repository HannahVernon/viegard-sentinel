namespace Viegard.Application.Queues;

/// <summary>Point-in-time statistics for a work queue, used by queue health monitoring (D-0012).</summary>
public sealed record WorkQueueStats
{
    public required string QueueName { get; init; }

    /// <summary>Messages waiting to be leased.</summary>
    public required int Depth { get; init; }

    /// <summary>Messages currently leased and not yet completed or abandoned.</summary>
    public required int InFlight { get; init; }

    /// <summary>Enqueue time of the oldest pending message; null when the queue is empty.</summary>
    public DateTimeOffset? OldestPendingEnqueuedAt { get; init; }

    public required long TotalEnqueued { get; init; }

    public required long TotalCompleted { get; init; }

    public required long TotalAbandoned { get; init; }

    public required int DeadLetterCount { get; init; }
}

/// <summary>
/// A leased message.  The consumer must either complete (acknowledge) or
/// abandon (return for redelivery) every lease.
/// </summary>
public interface IWorkLease<out T>
{
    T Message { get; }

    /// <summary>How many times this message has been delivered, including this lease.</summary>
    int DeliveryCount { get; }

    /// <summary>Acknowledge successful processing; the message is removed permanently.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the message for redelivery.  Messages exceeding the queue's
    /// maximum delivery count are moved to the dead-letter collection instead.
    /// </summary>
    ValueTask AbandonAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Broker-semantics work queue port (see ARCHITECTURE.md assumption 3).
/// Messages must be small, versioned, serializable records carrying entity
/// IDs, and consumers must be idempotent, so a durable or brokered
/// implementation can replace the in-process one without code changes.
/// </summary>
public interface IWorkQueue<T>
{
    string QueueName { get; }

    ValueTask EnqueueAsync(T message, CancellationToken cancellationToken = default);

    /// <summary>Lease the next message, waiting until one is available or cancellation.</summary>
    ValueTask<IWorkLease<T>> LeaseAsync(CancellationToken cancellationToken = default);

    WorkQueueStats GetStats();
}
