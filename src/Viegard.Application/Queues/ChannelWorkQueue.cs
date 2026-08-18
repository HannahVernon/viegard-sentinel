using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Viegard.Application.Queues;

/// <summary>
/// In-process implementation of the broker-semantics work queue port using a
/// bounded channel (ARCHITECTURE.md assumption 3).  Not durable: contents are
/// lost on process exit, which is acceptable because messages carry entity
/// IDs whose underlying records are persisted, and ingestion offsets allow
/// safe replay.
/// </summary>
public sealed class ChannelWorkQueue<T> : IWorkQueue<T>
{
    private sealed record WorkItem(T Message, int DeliveryCount, DateTimeOffset EnqueuedAt, long Sequence);

    private readonly Channel<WorkItem> _channel;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _pending = new();
    private readonly ConcurrentQueue<T> _deadLetters = new();
    private readonly int _maxDeliveryCount;
    private readonly TimeProvider _time;

    private long _sequence;
    private long _totalEnqueued;
    private long _totalCompleted;
    private long _totalAbandoned;
    private int _inFlight;
    private int _deadLetterCount;

    public ChannelWorkQueue(string queueName, int capacity = 1024, int maxDeliveryCount = 5, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeliveryCount, 1);

        QueueName = queueName;
        _maxDeliveryCount = maxDeliveryCount;
        _time = timeProvider ?? TimeProvider.System;
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string QueueName { get; }

    /// <summary>Messages that exceeded the maximum delivery count.</summary>
    public IReadOnlyCollection<T> DeadLetters => _deadLetters.ToArray();

    public async ValueTask EnqueueAsync(T message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var item = new WorkItem(message, DeliveryCount: 1, _time.GetUtcNow(), Interlocked.Increment(ref _sequence));
        await WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalEnqueued);
    }

    public async ValueTask<IWorkLease<T>> LeaseAsync(CancellationToken cancellationToken = default)
    {
        var item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        _pending.TryRemove(item.Sequence, out _);
        Interlocked.Increment(ref _inFlight);
        return new Lease(this, item);
    }

    public WorkQueueStats GetStats()
    {
        var pendingSnapshot = _pending.Values;
        return new WorkQueueStats
        {
            QueueName = QueueName,
            Depth = _pending.Count,
            InFlight = Volatile.Read(ref _inFlight),
            OldestPendingEnqueuedAt = pendingSnapshot.Count > 0 ? pendingSnapshot.Min() : null,
            TotalEnqueued = Interlocked.Read(ref _totalEnqueued),
            TotalCompleted = Interlocked.Read(ref _totalCompleted),
            TotalAbandoned = Interlocked.Read(ref _totalAbandoned),
            DeadLetterCount = Volatile.Read(ref _deadLetterCount),
        };
    }

    private async ValueTask WriteAsync(WorkItem item, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        _pending[item.Sequence] = item.EnqueuedAt;
    }

    private async ValueTask AbandonCoreAsync(WorkItem item, CancellationToken cancellationToken)
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _totalAbandoned);

        if (item.DeliveryCount >= _maxDeliveryCount)
        {
            _deadLetters.Enqueue(item.Message);
            Interlocked.Increment(ref _deadLetterCount);
            return;
        }

        // Redeliver with the original enqueue time so queue-age telemetry
        // reflects how long the message has truly waited.
        var redelivery = item with
        {
            DeliveryCount = item.DeliveryCount + 1,
            Sequence = Interlocked.Increment(ref _sequence),
        };
        await WriteAsync(redelivery, cancellationToken).ConfigureAwait(false);
    }

    private void CompleteCore()
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _totalCompleted);
    }

    private sealed class Lease(ChannelWorkQueue<T> queue, WorkItem item) : IWorkLease<T>
    {
        private int _settled;

        public T Message => item.Message;

        public int DeliveryCount => item.DeliveryCount;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            EnsureUnsettled();
            queue.CompleteCore();
            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonAsync(CancellationToken cancellationToken = default)
        {
            EnsureUnsettled();
            return queue.AbandonCoreAsync(item, cancellationToken);
        }

        private void EnsureUnsettled()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                throw new InvalidOperationException("This lease has already been completed or abandoned.");
            }
        }
    }
}
