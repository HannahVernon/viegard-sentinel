using System.Collections.Concurrent;

namespace Viegard.AdminApi.Auth;

/// <summary>
/// A step-up-gated POST that was captured because step-up was missing, kept so
/// it can be replayed verbatim once the operator completes verification.  The
/// form snapshot includes the antiforgery request token, which is reusable
/// within the session, so the replay re-validates against the intact cookie.
/// </summary>
public sealed record PendingStepUpAction(
    string Path,
    IReadOnlyList<KeyValuePair<string, string[]>> Form,
    DateTimeOffset ExpiresAt);

public interface IPendingStepUpActionStore
{
    void Save(Guid sessionId, PendingStepUpAction action);

    bool TryPeek(Guid sessionId, DateTimeOffset now, out PendingStepUpAction action);

    bool TryConsume(Guid sessionId, DateTimeOffset now, out PendingStepUpAction action);

    void Clear(Guid sessionId);
}

/// <summary>
/// Process-local pending-action stash keyed by admin session.  This mirrors the
/// current in-memory posture of the guardrail/rate state: it is single-instance
/// only, with a documented path to move to durable storage before the admin API
/// runs multi-instance (see D-0053).  Entries are single-use and time-bounded by
/// the configurable resume-stash TTL.
/// </summary>
public sealed class InMemoryPendingStepUpActionStore : IPendingStepUpActionStore
{
    private readonly ConcurrentDictionary<Guid, PendingStepUpAction> _actions = new();

    public void Save(Guid sessionId, PendingStepUpAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions[sessionId] = action;
    }

    public bool TryPeek(Guid sessionId, DateTimeOffset now, out PendingStepUpAction action)
    {
        if (_actions.TryGetValue(sessionId, out var stored) && stored.ExpiresAt > now)
        {
            action = stored;
            return true;
        }

        if (stored is not null)
        {
            _actions.TryRemove(sessionId, out _);
        }

        action = null!;
        return false;
    }

    public bool TryConsume(Guid sessionId, DateTimeOffset now, out PendingStepUpAction action)
    {
        if (_actions.TryRemove(sessionId, out var stored) && stored.ExpiresAt > now)
        {
            action = stored;
            return true;
        }

        action = null!;
        return false;
    }

    public void Clear(Guid sessionId) => _actions.TryRemove(sessionId, out _);
}
