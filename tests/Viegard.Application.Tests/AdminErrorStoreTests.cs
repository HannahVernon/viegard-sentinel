using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class AdminErrorStoreTests
{
    [Fact]
    public async Task Add_list_and_clear_round_trip_newest_first()
    {
        var store = new InMemoryAdminErrorStore();
        var older = Error(occurredAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = Error(occurredAt: DateTimeOffset.UtcNow);
        await store.AddAsync(older);
        await store.AddAsync(newer);

        var listed = await store.ListRecentAsync(10);

        Assert.Equal([newer.Id, older.Id], listed.Select(e => e.Id).ToArray());
        Assert.Equal(2, await store.ClearAsync());
        Assert.Empty(await store.ListRecentAsync(10));
    }

    [Fact]
    public async Task Store_keeps_only_the_newest_entries()
    {
        var store = new InMemoryAdminErrorStore();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < AdminError.KeepNewest + 2; i++)
        {
            await store.AddAsync(Error(occurredAt: start.AddSeconds(i)));
        }

        var listed = await store.ListRecentAsync(AdminError.KeepNewest + 10);

        Assert.Equal(AdminError.KeepNewest, listed.Count);
        // The two oldest entries were trimmed.
        Assert.Equal(start.AddSeconds(2), listed[^1].OccurredAt);
    }

    private static AdminError Error(DateTimeOffset occurredAt) => new()
    {
        Id = ViegardId.New(),
        OccurredAt = occurredAt,
        RequestId = "00-test",
        Path = "/events",
        Method = "GET",
        Username = "hannah",
        ExceptionType = "System.TimeoutException",
        Message = "Timeout during reading attempt",
        StackTrace = "at Somewhere",
    };
}
