using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class GuardrailStateStoreTests
{
    [Fact]
    public async Task Auto_action_counts_use_rolling_hour_and_day_windows()
    {
        var store = new InMemoryGuardrailStateStore();
        var now = DateTimeOffset.UtcNow;

        await store.RecordAutoActionAsync(now.AddMinutes(-30));
        await store.RecordAutoActionAsync(now.AddHours(-2));
        await store.RecordAutoActionAsync(now.AddHours(-25));

        var counts = await store.GetAutoActionCountsAsync(now);

        Assert.Equal(1, counts.LastHour);
        Assert.Equal(2, counts.LastDay);
    }

    [Fact]
    public async Task Consecutive_failures_reset_on_success_and_circuit_flag_is_tracked()
    {
        var store = new InMemoryGuardrailStateStore();
        var now = DateTimeOffset.UtcNow;

        await store.RecordActionFailureAsync(now.AddMinutes(-3));
        await store.RecordActionFailureAsync(now.AddMinutes(-2));
        Assert.Equal(2, await store.GetConsecutiveActionFailuresAsync());

        await store.RecordActionSuccessAsync(now.AddMinutes(-1));
        Assert.Equal(0, await store.GetConsecutiveActionFailuresAsync());

        await store.SetCircuitBreakerOpenAsync(true);
        Assert.True(await store.IsCircuitBreakerOpenAsync());

        await store.SetCircuitBreakerOpenAsync(false);
        Assert.False(await store.IsCircuitBreakerOpenAsync());
    }

    [Fact]
    public async Task Incident_history_counts_unique_incidents_inside_lookback()
    {
        var store = new InMemoryGuardrailStateStore();
        var now = DateTimeOffset.UtcNow;
        var duplicateIncident = Guid.NewGuid();

        await store.RecordIncidentAsync("198.51.100.77", duplicateIncident, now.AddDays(-1));
        await store.RecordIncidentAsync("198.51.100.77", duplicateIncident, now.AddHours(-1));
        await store.RecordIncidentAsync("::ffff:198.51.100.77", Guid.NewGuid(), now.AddHours(-2));
        await store.RecordIncidentAsync("198.51.100.77", Guid.NewGuid(), now.AddDays(-8));

        var count = await store.GetIncidentCountAsync(
            "198.51.100.77",
            now.AddDays(-7),
            now);

        Assert.Equal(2, count);
    }
}
