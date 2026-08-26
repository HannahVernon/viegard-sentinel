using System.Collections.Concurrent;
using Viegard.Application.Audit;
using Viegard.Application.Stores;
using Viegard.Domain.Actions;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Feedback;
using Viegard.Domain.Incidents;

namespace Viegard.Persistence.InMemory;

/// <summary>
/// Development-only, non-durable store implementations.  These exist so the
/// skeleton and tests run before the database decision (D-0004) is made.
/// Not suitable for production: contents are lost on process exit.
/// </summary>
public sealed class InMemoryRawObservationStore : IRawObservationStore
{
    private readonly ConcurrentDictionary<Guid, RawObservation> _observations = new();
    private readonly ConcurrentDictionary<string, string> _payloads = new();

    public ValueTask AddAsync(RawObservation observation, string rawPayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        _observations[observation.Id] = observation;
        _payloads[observation.PayloadReference] = rawPayload;
        return ValueTask.CompletedTask;
    }

    public ValueTask<RawObservation?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_observations.GetValueOrDefault(id));

    public ValueTask<string?> GetPayloadAsync(string payloadReference, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_payloads.GetValueOrDefault(payloadReference));
}

public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<Guid, NormalizedEvent> _events = new();

    public ValueTask AddAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);
        _events[normalizedEvent.Id] = normalizedEvent;
        return ValueTask.CompletedTask;
    }

    public ValueTask<NormalizedEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_events.GetValueOrDefault(id));
}

public sealed class InMemoryIncidentStore : IIncidentStore
{
    private readonly ConcurrentDictionary<Guid, Incident> _incidents = new();

    public ValueTask UpsertAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        _incidents[incident.Id] = incident;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Incident?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_incidents.GetValueOrDefault(id));

    public ValueTask<Incident?> FindOpenByCorrelationKeyAsync(string correlationKey, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_incidents.Values
            .Where(i => i.State == IncidentState.Open && i.CorrelationKey == correlationKey)
            .OrderByDescending(i => i.WindowEnd)
            .FirstOrDefault());
}

public sealed class InMemoryClassificationStore : IClassificationStore
{
    private readonly ConcurrentDictionary<Guid, Classification> _classifications = new();

    public ValueTask AddAsync(Classification classification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        _classifications[classification.Id] = classification;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Classification?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_classifications.GetValueOrDefault(id));
}

public sealed class InMemoryDecisionStore : IDecisionStore
{
    private readonly ConcurrentDictionary<Guid, Decision> _decisions = new();

    public ValueTask AddAsync(Decision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _decisions[decision.Id] = decision;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Decision?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_decisions.GetValueOrDefault(id));
}

public sealed class InMemoryActionStore : IActionStore
{
    private readonly ConcurrentDictionary<Guid, ActionRecord> _actions = new();

    public ValueTask UpsertAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionRecord);
        _actions[actionRecord.Id] = actionRecord;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_actions.GetValueOrDefault(id));
}

public sealed class InMemoryCorrectionStore : ICorrectionStore
{
    private readonly ConcurrentDictionary<Guid, Correction> _corrections = new();

    public ValueTask AddAsync(Correction correction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correction);
        _corrections[correction.Id] = correction;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Correction>> GetForClassificationAsync(Guid classificationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<Correction>>(
            _corrections.Values.Where(c => c.ClassificationId == classificationId).OrderBy(c => c.CreatedAt).ToList());
}

/// <summary>Development-only audit ledger; see class remarks on the stores above.</summary>
public sealed class InMemoryAuditLedger : IAuditLedger
{
    private readonly ConcurrentQueue<AuditRecord> _records = new();

    public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records.Enqueue(record);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<AuditRecord> Snapshot() => _records.ToArray();
}

public sealed class InMemoryAdminUserStore : IAdminUserStore
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, AdminUser> _users = new();
    private readonly ConcurrentDictionary<Guid, AdminTotpSecret> _totpSecrets = new();
    private readonly ConcurrentDictionary<Guid, AdminRecoveryCode> _recoveryCodes = new();

    public ValueTask<bool> AnyUsersAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(!_users.IsEmpty);

    public ValueTask<AdminUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_users.GetValueOrDefault(id));

    public ValueTask<AdminUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsername(username);
        return ValueTask.FromResult(_users.Values.FirstOrDefault(u => u.Username == normalized));
    }

    public ValueTask CreateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var normalized = user with { Username = NormalizeUsername(user.Username) };
        lock (_sync)
        {
            if (_users.Values.Any(u => u.Username == normalized.Username))
            {
                throw new InvalidOperationException($"Admin user '{normalized.Username}' already exists.");
            }

            _users[normalized.Id] = normalized;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        _users[user.Id] = user with { Username = NormalizeUsername(user.Username) };
        return ValueTask.CompletedTask;
    }

    public ValueTask<AdminTotpSecret?> GetTotpSecretAsync(Guid userId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_totpSecrets.GetValueOrDefault(userId));

    public ValueTask UpsertTotpSecretAsync(AdminTotpSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _totpSecrets[secret.UserId] = secret;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TrySetTotpLastAcceptedStepAsync(
        Guid userId,
        long acceptedStep,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_totpSecrets.TryGetValue(userId, out var secret)
                || secret.LastAcceptedStep >= acceptedStep)
            {
                return ValueTask.FromResult(false);
            }

            _totpSecrets[userId] = secret with { LastAcceptedStep = acceptedStep };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<AdminRecoveryCode>> GetRecoveryCodesAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AdminRecoveryCode>>(
            _recoveryCodes.Values.Where(c => c.UserId == userId).OrderBy(c => c.CreatedAt).ToList());

    public ValueTask ReplaceRecoveryCodesAsync(
        Guid userId,
        IReadOnlyList<AdminRecoveryCode> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        lock (_sync)
        {
            foreach (var existing in _recoveryCodes.Values.Where(c => c.UserId == userId).Select(c => c.Id).ToList())
            {
                _recoveryCodes.TryRemove(existing, out _);
            }

            foreach (var code in codes)
            {
                _recoveryCodes[code.Id] = code;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryMarkRecoveryCodeUsedAsync(
        Guid codeId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_recoveryCodes.TryGetValue(codeId, out var code) || code.UsedAt is not null)
            {
                return ValueTask.FromResult(false);
            }

            _recoveryCodes[codeId] = code with { UsedAt = usedAt.ToUniversalTime() };
            return ValueTask.FromResult(true);
        }
    }

    private static string NormalizeUsername(string username) =>
        username.Trim().ToLowerInvariant();
}

public sealed class InMemoryAdminSessionStore : IAdminSessionStore
{
    private readonly ConcurrentDictionary<Guid, AdminSession> _sessions = new();

    public ValueTask CreateAsync(AdminSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.Id] = session;
        return ValueTask.CompletedTask;
    }

    public ValueTask<AdminSession?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_sessions.GetValueOrDefault(id));

    public ValueTask<IReadOnlyList<AdminSession>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult<IReadOnlyList<AdminSession>>(
            _sessions.Values.Where(s =>
                s.UserId == userId
                && s.RevokedAt is null
                && s.AbsoluteExpiresAt > now
                && s.IdleExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt).ToList());
    }

    public ValueTask UpdateActivityAsync(
        Guid id,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(id, out var session) && session.RevokedAt is null)
        {
            _sessions[id] = session with
            {
                LastSeenAt = lastSeenAt.ToUniversalTime(),
                IdleExpiresAt = idleExpiresAt.ToUniversalTime(),
            };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StampStepUpAsync(Guid id, DateTimeOffset stepUpAt, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(id, out var session) && session.RevokedAt is null)
        {
            _sessions[id] = session with { StepUpAt = stepUpAt.ToUniversalTime() };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(id, out var session) && session.RevokedAt is null)
        {
            _sessions[id] = session with { RevokedAt = revokedAt.ToUniversalTime() };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeForUserAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        Guid? exceptSessionId = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var (id, session) in _sessions)
        {
            if (session.UserId == userId
                && session.RevokedAt is null
                && id != exceptSessionId)
            {
                _sessions[id] = session with { RevokedAt = revokedAt.ToUniversalTime() };
            }
        }

        return ValueTask.CompletedTask;
    }
}
