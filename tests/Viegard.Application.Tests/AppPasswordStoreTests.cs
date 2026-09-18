using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class AppPasswordStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Create_list_and_lookup_roundtrip()
    {
        var store = new InMemoryAppPasswordStore();
        var userId = ViegardId.New();
        var first = AppPassword(userId, "copilot-cli");
        var second = AppPassword(userId, "backup-script") with { CreatedAt = Now.AddMinutes(1) };
        var foreign = AppPassword(ViegardId.New(), "other-user");
        await store.CreateAsync(first);
        await store.CreateAsync(second);
        await store.CreateAsync(foreign);

        var listed = await store.ListForUserAsync(userId);
        Assert.Equal([second.Id, first.Id], listed.Select(p => p.Id));

        var found = await store.GetByLookupKeyAsync(first.LookupKey);
        Assert.Equal(first.Id, found!.Id);
        Assert.Null(await store.GetByLookupKeyAsync("ffffffffffffffff"));
    }

    [Fact]
    public async Task Create_rejects_duplicate_lookup_keys()
    {
        var store = new InMemoryAppPasswordStore();
        var first = AppPassword(ViegardId.New(), "one");
        await store.CreateAsync(first);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAsync(AppPassword(ViegardId.New(), "two") with { LookupKey = first.LookupKey }));
    }

    [Fact]
    public async Task Revoke_requires_matching_owner_and_is_idempotent()
    {
        var store = new InMemoryAppPasswordStore();
        var userId = ViegardId.New();
        var appPassword = AppPassword(userId, "copilot-cli");
        await store.CreateAsync(appPassword);

        Assert.False(await store.RevokeAsync(appPassword.Id, ViegardId.New(), Now));
        Assert.Null((await store.GetByLookupKeyAsync(appPassword.LookupKey))!.RevokedAt);

        Assert.True(await store.RevokeAsync(appPassword.Id, userId, Now));
        Assert.NotNull((await store.GetByLookupKeyAsync(appPassword.LookupKey))!.RevokedAt);
        Assert.False(await store.RevokeAsync(appPassword.Id, userId, Now));
    }

    [Fact]
    public async Task UpdateLastUsed_stamps_the_token()
    {
        var store = new InMemoryAppPasswordStore();
        var appPassword = AppPassword(ViegardId.New(), "copilot-cli");
        await store.CreateAsync(appPassword);

        await store.UpdateLastUsedAsync(appPassword.Id, Now);

        Assert.Equal(Now.ToUniversalTime(), (await store.GetByLookupKeyAsync(appPassword.LookupKey))!.LastUsedAt);
    }

    [Fact]
    public void IsUsable_honors_revocation_and_expiry()
    {
        var appPassword = AppPassword(ViegardId.New(), "copilot-cli");

        Assert.True(appPassword.IsUsable(Now));
        Assert.True((appPassword with { ExpiresAt = Now.AddDays(1) }).IsUsable(Now));
        Assert.False((appPassword with { ExpiresAt = Now.AddSeconds(-1) }).IsUsable(Now));
        Assert.False((appPassword with { RevokedAt = Now }).IsUsable(Now));
    }

    private static AppPassword AppPassword(Guid userId, string name)
    {
        var generated = Viegard.Application.Auth.AppPasswordTokenFormat.Generate();
        return new AppPassword
        {
            Id = ViegardId.New(),
            UserId = userId,
            Name = name,
            LookupKey = generated.LookupKey,
            SecretHash = generated.SecretHash,
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(90),
        };
    }
}
