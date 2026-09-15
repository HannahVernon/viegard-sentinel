using Viegard.Application.Retention;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class RetentionSettingsStoreTests
{
    [Fact]
    public async Task In_memory_store_round_trips_and_detects_optimistic_concurrency_conflict()
    {
        var store = new InMemoryRetentionSettingsStore();
        var now = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);

        var saved = await store.UpsertAsync(
            SettingsWith((RetentionTarget.Events, 90)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: now);

        Assert.True(saved.Succeeded);
        Assert.Equal(1, saved.Settings!.Version);
        Assert.Equal("hannah", saved.Settings.UpdatedBy);
        Assert.Equal(90, (await store.GetAsync())!.EventsDays);

        var conflict = await store.UpsertAsync(
            SettingsWith((RetentionTarget.Events, 30)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: now.AddMinutes(1));

        Assert.False(conflict.Succeeded);
        Assert.Equal(90, conflict.Settings!.EventsDays);
        Assert.Equal(1, conflict.Settings.Version);
    }

    [Fact]
    public async Task In_memory_seed_uses_options_once_and_never_overwrites_existing_row()
    {
        var store = new InMemoryRetentionSettingsStore();
        var seededAt = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);

        var seeded = await store.SeedIfMissingAsync(
            new RetentionOptions
            {
                EventsDays = 90,
                AuditRecordsDays = 365,
            },
            seededAt);

        Assert.NotNull(seeded);
        Assert.Equal(90, seeded.EventsDays);
        Assert.Equal(365, seeded.AuditRecordsDays);
        Assert.Equal(seededAt, seeded.SeededAt);

        var second = await store.SeedIfMissingAsync(
            new RetentionOptions
            {
                EventsDays = 7,
                AuditRecordsDays = null,
            },
            seededAt.AddMinutes(1));

        Assert.Equal(90, second!.EventsDays);
        Assert.Equal(365, second.AuditRecordsDays);
        Assert.Equal(seededAt, second.SeededAt);
    }

    [Fact]
    public async Task In_memory_admin_edit_before_seed_wins_over_later_seed()
    {
        var store = new InMemoryRetentionSettingsStore();
        var now = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);

        var admin = await store.UpsertAsync(
            SettingsWith((RetentionTarget.Events, 45)),
            expectedVersion: 0,
            updatedBy: "hannah",
            updatedAt: now);
        Assert.True(admin.Succeeded);

        var seeded = await store.SeedIfMissingAsync(
            new RetentionOptions { EventsDays = 90 },
            now.AddMinutes(1));

        Assert.Equal(45, seeded!.EventsDays);
        Assert.Null(seeded.SeededAt);
        Assert.Equal("hannah", seeded.UpdatedBy);
    }

    private static RetentionSettings SettingsWith(params (RetentionTarget Target, int? Days)[] values)
    {
        var settings = new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };
        foreach (var (target, days) in values)
        {
            settings = settings.WithDays(target, days);
        }

        return settings;
    }
}
