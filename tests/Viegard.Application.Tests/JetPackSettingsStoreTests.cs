using Viegard.Application.Configuration;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class JetPackSettingsStoreTests
{
    [Fact]
    public async Task In_memory_settings_store_round_trips_and_detects_optimistic_concurrency_conflict()
    {
        var store = new InMemoryJetPackFeedSettingsStore();
        var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

        var saved = await store.UpsertAsync(
            Settings(addressListName: "jetpack_servers"),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: now);

        Assert.True(saved.Succeeded);
        Assert.Equal(1, saved.Settings!.Version);
        Assert.Equal("hannah", saved.Settings.UpdatedBy);
        Assert.Equal("jetpack_servers", (await store.GetAsync())!.AddressListName);

        var conflict = await store.UpsertAsync(
            Settings(addressListName: "jetpack_custom"),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: now.AddMinutes(1));

        Assert.False(conflict.Succeeded);
        Assert.Equal("jetpack_servers", conflict.Settings!.AddressListName);
        Assert.Equal(1, conflict.Settings.Version);
    }

    [Fact]
    public async Task In_memory_seed_uses_options_once_and_never_overwrites_existing_row()
    {
        var store = new InMemoryJetPackFeedSettingsStore();
        var seededAt = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

        var seeded = await store.SeedIfMissingAsync(
            new JetPackFeedOptions
            {
                FeedUrl = "https://jetpack.example/feed.json",
                FetchInterval = TimeSpan.FromHours(2),
                Enabled = false,
                AddressListName = "seeded_list",
            },
            seededAt);

        Assert.NotNull(seeded);
        Assert.Equal("https://jetpack.example/feed.json", seeded!.FeedUrl);
        Assert.Equal(TimeSpan.FromHours(2), seeded.FetchInterval);
        Assert.False(seeded.Enabled);
        Assert.Equal("seeded_list", seeded.AddressListName);
        Assert.Equal(seededAt, seeded.SeededAt);

        var second = await store.SeedIfMissingAsync(
            new JetPackFeedOptions
            {
                FeedUrl = "https://ignored.example/feed.json",
                FetchInterval = TimeSpan.FromMinutes(5),
                Enabled = true,
                AddressListName = "ignored",
            },
            seededAt.AddMinutes(1));

        Assert.Equal("https://jetpack.example/feed.json", second!.FeedUrl);
        Assert.Equal(TimeSpan.FromHours(2), second.FetchInterval);
        Assert.False(second.Enabled);
        Assert.Equal("seeded_list", second.AddressListName);
        Assert.Equal(seededAt, second.SeededAt);
    }

    [Fact]
    public async Task In_memory_desired_address_store_replaces_snapshot_with_diff_semantics()
    {
        var store = new InMemoryJetPackDesiredAddressStore();
        var firstSeen = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var secondSeen = firstSeen.AddHours(1);

        var first = await store.ReplaceSnapshotAsync(
            ["122.248.245.244/32", "192.0.80.0/20"],
            firstSeen);

        Assert.Equal(2, first.Added);
        Assert.Equal(0, first.Removed);
        Assert.Equal(2, first.CurrentCount);

        var second = await store.ReplaceSnapshotAsync(
            ["192.0.80.0/20", "195.234.108.0/22"],
            secondSeen);

        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Removed);
        Assert.Equal(1, second.Updated);
        Assert.Equal(2, second.CurrentCount);

        var rows = await store.ListAsync();
        Assert.Equal(["192.0.80.0/20", "195.234.108.0/22"], rows.Select(row => row.Address).ToArray());
        Assert.Equal(firstSeen, rows[0].FirstSeenAt);
        Assert.Equal(secondSeen, rows[0].LastSeenAt);
    }

    private static JetPackFeedSettings Settings(
        string feedUrl = JetPackFeedSettings.DefaultFeedUrl,
        TimeSpan? fetchInterval = null,
        bool enabled = true,
        string addressListName = JetPackFeedSettings.DefaultAddressListName) => new()
    {
        FeedUrl = feedUrl,
        FetchInterval = fetchInterval ?? JetPackFeedSettings.DefaultFetchInterval,
        Enabled = enabled,
        AddressListName = addressListName,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
    };
}
