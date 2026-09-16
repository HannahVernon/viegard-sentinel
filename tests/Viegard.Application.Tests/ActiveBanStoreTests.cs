using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class InMemoryActiveBanStoreTests
{
    [Fact]
    public async Task Upsert_replaces_existing_row_by_canonical_ip()
    {
        var store = new InMemoryActiveBanStore();
        var now = new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.Zero);
        var first = Ban("2001:db8:0:0::1", now, now.AddMinutes(5));
        var second = Ban("2001:db8::1", now.AddMinutes(1), now.AddMinutes(10));

        await store.UpsertByIpAsync(first);
        var saved = await store.UpsertByIpAsync(second);

        Assert.Equal("2001:db8::1", saved.Ip);
        Assert.Equal(second.Id, saved.Id);
        Assert.Equal(second.DecisionId, saved.DecisionId);
        Assert.Equal(second.ActionId, saved.ActionId);
        Assert.Equal(second.ExpiresAt, saved.ExpiresAt);
        Assert.Equal(second.Id, (await store.GetByIpAsync("2001:db8:0:0:0:0:0:1"))!.Id);
        Assert.Single(await store.ListUnexpiredAsync(now));
    }

    [Fact]
    public async Task Remove_by_ip_reports_whether_a_row_existed()
    {
        var store = new InMemoryActiveBanStore();
        var now = new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.Zero);
        await store.UpsertByIpAsync(Ban("203.0.113.10", now, now.AddMinutes(5)));

        Assert.True(await store.RemoveByIpAsync("203.0.113.10"));
        Assert.False(await store.RemoveByIpAsync("203.0.113.10"));
        Assert.Null(await store.GetByIpAsync("203.0.113.10"));
    }

    [Fact]
    public async Task List_unexpired_excludes_expired_rows()
    {
        var store = new InMemoryActiveBanStore();
        var now = new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.Zero);
        var expired = Ban("203.0.113.10", now.AddMinutes(-10), now.AddSeconds(-1));
        var unexpired = Ban("203.0.113.11", now.AddMinutes(-1), now.AddMinutes(5));
        await store.UpsertByIpAsync(expired);
        await store.UpsertByIpAsync(unexpired);

        var listed = await store.ListUnexpiredAsync(now);

        Assert.Equal(unexpired.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task Delete_expired_removes_only_expired_rows()
    {
        var store = new InMemoryActiveBanStore();
        var now = new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.Zero);
        await store.UpsertByIpAsync(Ban("203.0.113.10", now.AddMinutes(-10), now));
        await store.UpsertByIpAsync(Ban("203.0.113.11", now.AddMinutes(-1), now.AddMinutes(5)));

        var deleted = await store.DeleteExpiredAsync(now);

        Assert.Equal(1, deleted);
        Assert.Null(await store.GetByIpAsync("203.0.113.10"));
        Assert.NotNull(await store.GetByIpAsync("203.0.113.11"));
    }

    private static ActiveBan Ban(string ip, DateTimeOffset createdAt, DateTimeOffset expiresAt) => new()
    {
        Id = ViegardId.New(),
        Ip = ip,
        CreatedAt = createdAt,
        ExpiresAt = expiresAt,
        DecisionId = ViegardId.New(),
        ActionId = ViegardId.New(),
    };
}
