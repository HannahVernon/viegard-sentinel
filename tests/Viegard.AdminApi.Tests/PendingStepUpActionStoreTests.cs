using Viegard.AdminApi.Auth;

namespace Viegard.AdminApi.Tests;

public sealed class PendingStepUpActionStoreTests
{
    private static PendingStepUpAction Action(DateTimeOffset expiresAt) => new(
        "/configuration/session-security",
        [new KeyValuePair<string, string[]>("stepUpValiditySeconds", ["240"])],
        expiresAt);

    [Fact]
    public void Saved_action_can_be_peeked_before_expiry()
    {
        var store = new InMemoryPendingStepUpActionStore();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        store.Save(sessionId, Action(now.AddMinutes(10)));

        Assert.True(store.TryPeek(sessionId, now, out var peeked));
        Assert.Equal("/configuration/session-security", peeked.Path);
        Assert.True(store.TryPeek(sessionId, now, out _), "Peek does not consume the action.");
    }

    [Fact]
    public void Consume_removes_the_action()
    {
        var store = new InMemoryPendingStepUpActionStore();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        store.Save(sessionId, Action(now.AddMinutes(10)));

        Assert.True(store.TryConsume(sessionId, now, out _));
        Assert.False(store.TryConsume(sessionId, now, out _), "A consumed action cannot be consumed again.");
        Assert.False(store.TryPeek(sessionId, now, out _));
    }

    [Fact]
    public void Expired_action_is_not_returned_and_is_purged()
    {
        var store = new InMemoryPendingStepUpActionStore();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        store.Save(sessionId, Action(now.AddMinutes(-1)));

        Assert.False(store.TryPeek(sessionId, now, out _));
        Assert.False(store.TryConsume(sessionId, now, out _));
    }

    [Fact]
    public void Save_overwrites_the_previous_action_for_a_session()
    {
        var store = new InMemoryPendingStepUpActionStore();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        store.Save(sessionId, Action(now.AddMinutes(10)));
        store.Save(sessionId, new PendingStepUpAction("/decisions/review", [], now.AddMinutes(10)));

        Assert.True(store.TryPeek(sessionId, now, out var peeked));
        Assert.Equal("/decisions/review", peeked.Path);
    }

    [Fact]
    public void Clear_removes_a_pending_action()
    {
        var store = new InMemoryPendingStepUpActionStore();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        store.Save(sessionId, Action(now.AddMinutes(10)));

        store.Clear(sessionId);

        Assert.False(store.TryPeek(sessionId, now, out _));
    }
}
