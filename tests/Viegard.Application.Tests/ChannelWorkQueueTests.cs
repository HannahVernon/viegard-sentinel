using Viegard.Application.Queues;

namespace Viegard.Application.Tests;

public sealed class ChannelWorkQueueTests
{
    [Fact]
    public async Task Enqueue_lease_complete_roundtrip_updates_stats()
    {
        var queue = new ChannelWorkQueue<string>("test");

        await queue.EnqueueAsync("message-1");
        var lease = await queue.LeaseAsync();
        Assert.Equal("message-1", lease.Message);
        Assert.Equal(1, lease.DeliveryCount);
        await lease.CompleteAsync();

        var stats = queue.GetStatsCore();
        Assert.Equal(0, stats.Depth);
        Assert.Equal(0, stats.InFlight);
        Assert.Equal(1, stats.TotalEnqueued);
        Assert.Equal(1, stats.TotalCompleted);
        Assert.Equal(0, stats.TotalAbandoned);
        Assert.Equal(0, stats.DeadLetterCount);
        Assert.Null(stats.OldestPendingEnqueuedAt);
    }

    [Fact]
    public async Task Messages_are_delivered_in_fifo_order()
    {
        var queue = new ChannelWorkQueue<int>("test");
        await queue.EnqueueAsync(1);
        await queue.EnqueueAsync(2);
        await queue.EnqueueAsync(3);

        var first = await queue.LeaseAsync();
        var second = await queue.LeaseAsync();
        var third = await queue.LeaseAsync();

        Assert.Equal(1, first.Message);
        Assert.Equal(2, second.Message);
        Assert.Equal(3, third.Message);
    }

    [Fact]
    public async Task Abandon_redelivers_with_incremented_delivery_count()
    {
        var queue = new ChannelWorkQueue<string>("test");
        await queue.EnqueueAsync("retry-me");

        var lease = await queue.LeaseAsync();
        await lease.AbandonAsync();

        var redelivery = await queue.LeaseAsync();
        Assert.Equal("retry-me", redelivery.Message);
        Assert.Equal(2, redelivery.DeliveryCount);
        Assert.Equal(1, queue.GetStatsCore().TotalAbandoned);
    }

    [Fact]
    public async Task Message_exceeding_max_deliveries_is_dead_lettered()
    {
        var queue = new ChannelWorkQueue<string>("test", maxDeliveryCount: 2);
        await queue.EnqueueAsync("poison");

        var first = await queue.LeaseAsync();
        await first.AbandonAsync();
        var second = await queue.LeaseAsync();
        Assert.Equal(2, second.DeliveryCount);
        await second.AbandonAsync();

        var stats = queue.GetStatsCore();
        Assert.Equal(1, stats.DeadLetterCount);
        Assert.Equal(0, stats.Depth);
        Assert.Contains("poison", queue.DeadLetters);
    }

    [Fact]
    public async Task Oldest_pending_age_is_tracked_while_messages_wait()
    {
        var queue = new ChannelWorkQueue<string>("test");
        var before = DateTimeOffset.UtcNow;
        await queue.EnqueueAsync("waiting");

        var stats = queue.GetStatsCore();
        Assert.NotNull(stats.OldestPendingEnqueuedAt);
        Assert.InRange(stats.OldestPendingEnqueuedAt.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Redelivered_message_keeps_original_enqueue_time_for_age_telemetry()
    {
        var queue = new ChannelWorkQueue<string>("test");
        await queue.EnqueueAsync("aging");
        var originalOldest = queue.GetStatsCore().OldestPendingEnqueuedAt;
        Assert.NotNull(originalOldest);

        var lease = await queue.LeaseAsync();
        await Task.Delay(20);
        await lease.AbandonAsync();

        var stats = queue.GetStatsCore();
        Assert.Equal(originalOldest, stats.OldestPendingEnqueuedAt);
    }

    [Fact]
    public async Task Lease_cannot_be_settled_twice()
    {
        var queue = new ChannelWorkQueue<string>("test");
        await queue.EnqueueAsync("once");
        var lease = await queue.LeaseAsync();
        await lease.CompleteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.CompleteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.AbandonAsync());
    }

    [Fact]
    public async Task Lease_waits_until_cancelled_when_queue_is_empty()
    {
        var queue = new ChannelWorkQueue<string>("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queue.LeaseAsync(cts.Token));
    }
}
