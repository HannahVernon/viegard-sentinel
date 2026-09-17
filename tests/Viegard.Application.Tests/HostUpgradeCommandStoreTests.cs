using Microsoft.Extensions.Time.Testing;
using Viegard.Application.Configuration;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class HostUpgradeCommandStoreTests
{
    [Fact]
    public async Task In_memory_store_enforces_single_flight_and_cooldown()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero));
        var store = new InMemoryHostUpgradeCommandStore(time);

        var requested = await store.RequestAsync("vm", "hannah");

        var pendingReject = await Assert.ThrowsAsync<HostUpgradeCommandRejectedException>(async () =>
            await store.RequestAsync("vm", "hannah"));
        Assert.Equal(HostUpgradeCommandRejectionReason.SingleFlight, pendingReject.Reason);

        var claimed = await store.ClaimNextPendingAsync("vm");
        Assert.NotNull(claimed);
        Assert.Equal(requested.Id, claimed.Id);
        Assert.Equal(HostUpgradeCommandStatus.Running, claimed.Status);
        Assert.NotNull(claimed.StartedAt);

        var runningReject = await Assert.ThrowsAsync<HostUpgradeCommandRejectedException>(async () =>
            await store.RequestAsync("vm", "hannah"));
        Assert.Equal(HostUpgradeCommandRejectionReason.SingleFlight, runningReject.Reason);

        var completed = await store.CompleteAsync(requested.Id, succeeded: true, detail: "already up to date");
        Assert.NotNull(completed);
        Assert.Equal(HostUpgradeCommandStatus.Succeeded, completed.Status);
        Assert.Equal("already up to date", completed.Detail);

        var cooldownReject = await Assert.ThrowsAsync<HostUpgradeCommandRejectedException>(async () =>
            await store.RequestAsync("vm", "hannah"));
        Assert.Equal(HostUpgradeCommandRejectionReason.Cooldown, cooldownReject.Reason);
        Assert.NotNull(cooldownReject.RetryAt);

        time.Advance(HostUpgradeCommandPolicy.Cooldown.Add(TimeSpan.FromSeconds(1)));
        var next = await store.RequestAsync("vm", "hannah");
        Assert.NotEqual(requested.Id, next.Id);
        Assert.Equal(HostUpgradeCommandStatus.Pending, next.Status);
    }

    [Fact]
    public async Task In_memory_store_lists_default_target_then_distinct_recent_targets()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 22, 0, 0, TimeSpan.Zero));
        var store = new InMemoryHostUpgradeCommandStore(time);

        var firstSatellite = await store.RequestAsync("sat-a", "hannah");
        var firstClaim = await store.ClaimNextPendingAsync("sat-a");
        Assert.NotNull(firstClaim);
        await store.CompleteAsync(firstSatellite.Id, succeeded: true, detail: "ok");

        time.Advance(HostUpgradeCommandPolicy.Cooldown.Add(TimeSpan.FromSeconds(1)));
        var secondSatellite = await store.RequestAsync("sat-b", "hannah");
        var secondClaim = await store.ClaimNextPendingAsync("sat-b");
        Assert.NotNull(secondClaim);
        await store.CompleteAsync(secondSatellite.Id, succeeded: true, detail: "ok");

        time.Advance(HostUpgradeCommandPolicy.Cooldown.Add(TimeSpan.FromSeconds(1)));
        await store.RequestAsync("sat-a", "hannah");

        var targets = await store.ListTargetsAsync();

        Assert.Equal(["vm", "sat-a", "sat-b"], targets);
    }

    [Fact]
    public void Pending_stale_helper_flags_only_old_pending_commands()
    {
        var requestedAt = new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero);
        var oldEnough = HostUpgradeCommandPolicy.NewRequest("vm", "hannah", requestedAt);
        var recent = oldEnough with { RequestedAt = requestedAt.AddMinutes(2) };
        var running = oldEnough with { Status = HostUpgradeCommandStatus.Running };
        var now = requestedAt.Add(HostUpgradeCommandPolicy.PendingStaleAfter).AddSeconds(1);

        Assert.True(HostUpgradeCommandPolicy.IsPendingStale(oldEnough, now));
        Assert.False(HostUpgradeCommandPolicy.IsPendingStale(recent, now));
        Assert.False(HostUpgradeCommandPolicy.IsPendingStale(running, now));
    }
}
