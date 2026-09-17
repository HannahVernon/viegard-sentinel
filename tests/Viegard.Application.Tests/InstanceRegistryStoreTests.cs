using Viegard.Domain.Health;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class InstanceRegistryStoreTests
{
    [Fact]
    public async Task In_memory_store_upserts_by_instance_id_and_lists_in_instance_order()
    {
        var store = new InMemoryInstanceRegistryStore();
        var now = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
        var first = Registration("pipeline-b", "1.0.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", now);
        var second = Registration("pipeline-a", "1.0.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", now);
        var updatedFirst = first with
        {
            Version = "1.0.1+cccccccccccccccccccccccccccccccccccccccc",
            CommitSha = "cccccccccccccccccccccccccccccccccccccccc",
            UpgradeTarget = "satellite-b",
            ReportedAt = now.AddMinutes(1),
        };

        await store.UpsertAsync(first);
        await store.UpsertAsync(second);
        await store.UpsertAsync(updatedFirst);

        var registrations = await store.ListAsync();

        Assert.Equal(["pipeline-a", "pipeline-b"], registrations.Select(r => r.InstanceId).ToArray());
        var restored = registrations.Single(r => r.InstanceId == "pipeline-b");
        Assert.Equal("1.0.1+cccccccccccccccccccccccccccccccccccccccc", restored.Version);
        Assert.Equal("satellite-b", restored.UpgradeTarget);
        Assert.Equal(now.AddMinutes(1), restored.ReportedAt);
    }

    [Fact]
    public async Task In_memory_store_deletes_only_rows_reported_before_the_cutoff()
    {
        var store = new InMemoryInstanceRegistryStore();
        var now = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
        await store.UpsertAsync(Registration("admin-dead", "1.0.0", now.AddHours(-30)));
        await store.UpsertAsync(Registration("admin", "1.0.0", now));
        await store.UpsertAsync(Registration("pipeline-1", "1.0.0", now.AddMinutes(-1)));

        var removed = await store.DeleteStaleAsync(now.AddHours(-24));

        Assert.Equal(1, removed);
        var remaining = await store.ListAsync();
        Assert.Equal(["admin", "pipeline-1"], remaining.Select(r => r.InstanceId).ToArray());
    }

    private static InstanceRegistration Registration(string instanceId, string version, DateTimeOffset now) => new()
    {
        InstanceId = instanceId,
        Version = version,
        CommitSha = BuildVersion.Parse(version).CommitSha,
        Roles = "sources",
        UpgradeTarget = "satellite-a",
        HostName = "mail.example.com",
        StartedAt = now.AddHours(-1),
        ReportedAt = now,
    };
}
