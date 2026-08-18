using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.Application.Tests;

public sealed class QueueHealthEvaluatorTests
{
    private static readonly QueueHealthThresholds Thresholds = new()
    {
        AmberOldestPendingAge = TimeSpan.FromSeconds(30),
        RedOldestPendingAge = TimeSpan.FromMinutes(5),
        AmberDepth = 100,
        RedDepth = 1_000,
        TelemetryStaleAfter = TimeSpan.FromSeconds(60),
    };

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static QueueTelemetrySnapshot Snapshot(
        int depth = 0,
        DateTimeOffset? oldest = null,
        int deadLetters = 0,
        DateTimeOffset? capturedAt = null) => new()
    {
        InstanceId = "host-1",
        QueueName = "events",
        Depth = depth,
        InFlight = 0,
        OldestPendingEnqueuedAt = oldest,
        TotalEnqueued = 10,
        TotalCompleted = 10,
        TotalAbandoned = 0,
        DeadLetterCount = deadLetters,
        CapturedAt = capturedAt ?? Now,
    };

    private readonly QueueHealthEvaluator _evaluator = new(Thresholds);

    [Fact]
    public void Fresh_shallow_queue_is_green()
    {
        var status = _evaluator.Evaluate(Snapshot(), Now);
        Assert.Equal(TrafficLight.Green, status.Light);
    }

    [Fact]
    public void Stale_telemetry_is_red_even_when_numbers_look_healthy()
    {
        var status = _evaluator.Evaluate(Snapshot(capturedAt: Now.AddMinutes(-3)), Now);

        Assert.Equal(TrafficLight.Red, status.Light);
        Assert.Contains(status.Reasons, r => r.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Oldest_message_beyond_amber_threshold_is_amber()
    {
        var status = _evaluator.Evaluate(Snapshot(depth: 1, oldest: Now.AddSeconds(-45)), Now);
        Assert.Equal(TrafficLight.Amber, status.Light);
    }

    [Fact]
    public void Oldest_message_beyond_red_threshold_is_red()
    {
        var status = _evaluator.Evaluate(Snapshot(depth: 1, oldest: Now.AddMinutes(-10)), Now);
        Assert.Equal(TrafficLight.Red, status.Light);
    }

    [Fact]
    public void Depth_thresholds_escalate_status()
    {
        Assert.Equal(TrafficLight.Amber, _evaluator.Evaluate(Snapshot(depth: 150), Now).Light);
        Assert.Equal(TrafficLight.Red, _evaluator.Evaluate(Snapshot(depth: 2_000), Now).Light);
    }

    [Fact]
    public void Dead_letters_make_an_otherwise_green_queue_amber()
    {
        var status = _evaluator.Evaluate(Snapshot(deadLetters: 3), Now);

        Assert.Equal(TrafficLight.Amber, status.Light);
        Assert.Contains(status.Reasons, r => r.Contains("dead-letter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Red_age_wins_over_amber_depth()
    {
        var status = _evaluator.Evaluate(Snapshot(depth: 150, oldest: Now.AddMinutes(-10)), Now);
        Assert.Equal(TrafficLight.Red, status.Light);
    }
}
