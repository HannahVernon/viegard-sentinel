using System.Collections.Concurrent;
using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Auth;
using Viegard.Application.Stores;
using Viegard.Domain.Actions;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Configuration;
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

    public ValueTask<bool> AddAsync(RawObservation observation, string rawPayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!_payloads.TryAdd(observation.PayloadReference, rawPayload))
        {
            return ValueTask.FromResult(false);
        }

        _observations[observation.Id] = observation;
        return ValueTask.FromResult(true);
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

    public ValueTask<KeysetPage<NormalizedEvent>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        EventListFilter? filter = null,
        ListSort<EventSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.Page(
            InMemoryPaging.ApplyFilter(_events.Values, filter),
            beforeId,
            pageSize,
            e => e.Id,
            InMemoryPaging.EventComparison(sort)));

    public ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        EventListFilter? filter = null,
        ListSort<EventSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.PageCursor(
            InMemoryPaging.ApplyFilter(_events.Values, filter),
            pageNumber,
            pageSize,
            e => e.Id,
            InMemoryPaging.EventComparison(sort)));
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

    public ValueTask<IReadOnlyList<Incident>> FindByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<Incident>>(_incidents.Values
            .Where(i => i.EventIds.Contains(eventId))
            .OrderByDescending(i => i.WindowEnd)
            .ThenByDescending(i => i.Id)
            .ToList());

    public ValueTask<KeysetPage<Incident>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        IncidentListFilter? filter = null,
        ListSort<IncidentSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.Page(
            InMemoryPaging.ApplyFilter(_incidents.Values, filter),
            beforeId,
            pageSize,
            i => i.Id,
            InMemoryPaging.IncidentComparison(sort)));

    public ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        IncidentListFilter? filter = null,
        ListSort<IncidentSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.PageCursor(
            InMemoryPaging.ApplyFilter(_incidents.Values, filter),
            pageNumber,
            pageSize,
            i => i.Id,
            InMemoryPaging.IncidentComparison(sort)));
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

    internal int? TryGetSeverity(Guid id) =>
        _classifications.TryGetValue(id, out var classification) ? classification.Severity : null;

    public ValueTask<IReadOnlyList<Classification>> ListForSubjectAsync(
        ClassificationSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<Classification>>(_classifications.Values
            .Where(c => c.SubjectKind == subjectKind && c.SubjectId == subjectId)
            .OrderByDescending(c => c.Id)
            .ToList());

    public ValueTask<KeysetPage<Classification>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.Page(_classifications.Values, beforeId, pageSize, c => c.Id));
}

public sealed class InMemoryDecisionStore : IDecisionStore
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, Decision> _decisions = new();
    private readonly InMemoryClassificationStore? _classifications;

    public InMemoryDecisionStore(InMemoryClassificationStore? classifications = null)
    {
        _classifications = classifications;
    }

    public ValueTask AddAsync(Decision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _decisions[decision.Id] = decision;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Decision?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_decisions.GetValueOrDefault(id));

    public ValueTask<IReadOnlyList<Decision>> ListForClassificationAsync(
        Guid classificationId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<Decision>>(_decisions.Values
            .Where(d => d.ClassificationId == classificationId)
            .OrderByDescending(d => d.Id)
            .ToList());

    public ValueTask<KeysetPage<Decision>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        DecisionListFilter? filter = null,
        ListSort<DecisionSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.Page(
            ApplySeverityFilter(InMemoryPaging.ApplyFilter(_decisions.Values, filter), filter),
            beforeId,
            pageSize,
            d => d.Id,
            Comparison(sort)));

    public ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        DecisionListFilter? filter = null,
        ListSort<DecisionSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.PageCursor(
            ApplySeverityFilter(InMemoryPaging.ApplyFilter(_decisions.Values, filter), filter),
            pageNumber,
            pageSize,
            d => d.Id,
            Comparison(sort)));

    private int SeverityOf(Decision decision) =>
        _classifications?.TryGetSeverity(decision.ClassificationId) ?? 0;

    private IEnumerable<Decision> ApplySeverityFilter(IEnumerable<Decision> source, DecisionListFilter? filter)
    {
        var query = source;
        if (filter?.MinSeverity is { } minSeverity)
        {
            query = query.Where(d => _classifications?.TryGetSeverity(d.ClassificationId) is { } severity && severity >= minSeverity);
        }

        if (filter?.MaxSeverity is { } maxSeverity)
        {
            query = query.Where(d => _classifications?.TryGetSeverity(d.ClassificationId) is { } severity && severity <= maxSeverity);
        }

        if (filter?.UnreviewedOnly == true)
        {
            query = query.Where(d => d.ReviewedAt is null);
        }

        return query;
    }

    private Comparison<Decision>? Comparison(ListSort<DecisionSortColumn>? sort) =>
        sort is { Column: DecisionSortColumn.Severity, Direction: SortDirection.Asc or SortDirection.Desc } severitySort
            ? InMemoryPaging.CompareBy<Decision, int>(SeverityOf, d => d.Id, severitySort.Direction)
            : InMemoryPaging.DecisionComparison(sort);

    public ValueTask<Decision?> TryReviewAsync(
        Guid id,
        DecisionReviewOutcome outcome,
        string reviewedBy,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_decisions.TryGetValue(id, out var decision)
                || decision.Outcome != DecisionOutcome.RequireApproval
                || decision.ReviewedAt is not null)
            {
                return ValueTask.FromResult<Decision?>(null);
            }

            var reviewed = decision with
            {
                ReviewedBy = reviewedBy.Trim(),
                ReviewedAt = reviewedAt.ToUniversalTime(),
                ReviewOutcome = outcome,
            };
            _decisions[id] = reviewed;
            return ValueTask.FromResult<Decision?>(reviewed);
        }
    }

    public ValueTask<int> BulkRejectUnreviewedAsync(
        int maxSeverity,
        string reviewedBy,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var rejected = 0;
            foreach (var decision in _decisions.Values)
            {
                if (decision.Outcome != DecisionOutcome.RequireApproval
                    || decision.ReviewedAt is not null
                    || _classifications?.TryGetSeverity(decision.ClassificationId) is not { } severity
                    || severity > maxSeverity)
                {
                    continue;
                }

                _decisions[decision.Id] = decision with
                {
                    ReviewedBy = reviewedBy.Trim(),
                    ReviewedAt = reviewedAt.ToUniversalTime(),
                    ReviewOutcome = DecisionReviewOutcome.Rejected,
                };
                rejected++;
            }

            return ValueTask.FromResult(rejected);
        }
    }

    public ValueTask<int> CountUnreviewedAtOrBelowAsync(
        int maxSeverity,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_decisions.Values.Count(decision =>
            decision.Outcome == DecisionOutcome.RequireApproval
            && decision.ReviewedAt is null
            && _classifications?.TryGetSeverity(decision.ClassificationId) is { } severity
            && severity <= maxSeverity));
}

public sealed class InMemoryActionStore : IActionStore
{
    private readonly ConcurrentDictionary<Guid, ActionRecord> _actions = new();

    public ValueTask AddAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionRecord);
        if (!_actions.TryAdd(actionRecord.Id, actionRecord))
        {
            throw new InvalidOperationException($"Action {actionRecord.Id} already exists.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionRecord);
        _actions[actionRecord.Id] = actionRecord;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_actions.GetValueOrDefault(id));

    public ValueTask<IReadOnlyList<ActionRecord>> ListRecentByProviderAsync(
        string providerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        return ValueTask.FromResult<IReadOnlyList<ActionRecord>>(
            _actions.Values
                .Where(action => string.Equals(action.ProviderId, providerId, StringComparison.Ordinal))
                .OrderByDescending(action => action.RequestedAt)
                .ThenByDescending(action => action.Id)
                .Take(safeLimit)
                .ToList());
    }
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

    public ValueTask<KeysetPage<AuditRecord>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        AuditListFilter? filter = null,
        ListSort<AuditSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.Page(
            InMemoryPaging.ApplyFilter(_records.ToArray(), filter),
            beforeId,
            pageSize,
            r => r.Id,
            InMemoryPaging.AuditComparison(sort)));

    public ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        AuditListFilter? filter = null,
        ListSort<AuditSortColumn>? sort = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InMemoryPaging.PageCursor(
            InMemoryPaging.ApplyFilter(_records.ToArray(), filter),
            pageNumber,
            pageSize,
            r => r.Id,
            InMemoryPaging.AuditComparison(sort)));

    public IReadOnlyList<AuditRecord> Snapshot() => _records.ToArray();
}

public sealed class InMemoryCustomSignatureStore : ICustomSignatureStore
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, CustomSignature> _signatures = new();
    private readonly List<TaskCompletionSource<long>> _waiters = [];
    private long _changeVersion;
    private bool _seedChecked;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public ValueTask<IReadOnlyList<CustomSignature>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureSeeded();
        return ValueTask.FromResult<IReadOnlyList<CustomSignature>>(_signatures.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id)
            .ToList());
    }

    public ValueTask<KeysetPage<CustomSignature>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        SignatureListFilter? filter = null,
        ListSort<SignatureSortColumn>? sort = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSeeded();
        return ValueTask.FromResult(InMemoryPaging.Page(
            CustomSignatureFilter.Apply(_signatures.Values.ToList(), filter?.Text),
            beforeId,
            pageSize,
            s => s.Id,
            InMemoryPaging.SignatureComparison(sort)));
    }

    public ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        SignatureListFilter? filter = null,
        ListSort<SignatureSortColumn>? sort = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSeeded();
        return ValueTask.FromResult(InMemoryPaging.PageCursor(
            CustomSignatureFilter.Apply(_signatures.Values.ToList(), filter?.Text),
            pageNumber,
            pageSize,
            s => s.Id,
            InMemoryPaging.SignatureComparison(sort)));
    }

    public ValueTask<CustomSignature?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureSeeded();
        return ValueTask.FromResult(_signatures.GetValueOrDefault(id));
    }

    public ValueTask<CustomSignature> UpsertAsync(CustomSignature signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var normalized = CustomSignatureValidator.Normalize(signature);
        var validation = CustomSignatureValidator.Validate(normalized);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(CustomSignatureValidator.UniformError(validation));
        }

        EnsureSeeded();
        lock (_sync)
        {
            var duplicate = _signatures.Values.Any(s =>
                s.Id != normalized.Id && string.Equals(s.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                throw new InvalidOperationException("A signature with that name already exists.");
            }

            var now = DateTimeOffset.UtcNow;
            var saved = _signatures.TryGetValue(normalized.Id, out var existing)
                ? normalized with
                {
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = now,
                    Version = existing.Version + 1,
                }
                : normalized with
                {
                    CreatedAt = normalized.CreatedAt == default ? now : normalized.CreatedAt.ToUniversalTime(),
                    UpdatedAt = now,
                    Version = 1,
                };

            _signatures[saved.Id] = saved;
            SignalChanged();
            return ValueTask.FromResult(saved);
        }
    }

    public ValueTask<CustomSignature?> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_signatures.TryRemove(id, out var removed))
            {
                return ValueTask.FromResult<CustomSignature?>(null);
            }

            SignalChanged();
            return ValueTask.FromResult<CustomSignature?>(removed);
        }
    }

    public async ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<long> waiter;
        lock (_sync)
        {
            var current = CurrentChangeVersion;
            if (current != lastSeenVersion)
            {
                return current;
            }

            waiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = timeoutCts.Token.Register(() => waiter.TrySetResult(CurrentChangeVersion));
        timeoutCts.CancelAfter(timeout);
        var result = await waiter.Task.ConfigureAwait(false);

        lock (_sync)
        {
            _waiters.Remove(waiter);
        }

        return result;
    }

    private void EnsureSeeded()
    {
        if (_seedChecked)
        {
            return;
        }

        lock (_sync)
        {
            if (_seedChecked)
            {
                return;
            }

            if (_signatures.IsEmpty)
            {
                var now = DateTimeOffset.UtcNow;
                var seed = CustomSignatureSeeds.AftershipReferralBot(now);
                _signatures[seed.Id] = seed;
                SignalChanged();
            }

            _seedChecked = true;
        }
    }

    private void SignalChanged()
    {
        var version = Interlocked.Increment(ref _changeVersion);
        foreach (var waiter in _waiters.ToArray())
        {
            waiter.TrySetResult(version);
        }
    }
}

public sealed class InMemoryAdminUserStore : IAdminUserStore
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, AdminUser> _users = new();
    private readonly ConcurrentDictionary<Guid, AdminTotpSecret> _totpSecrets = new();
    private readonly ConcurrentDictionary<Guid, AdminRecoveryCode> _recoveryCodes = new();
    private readonly ConcurrentDictionary<Guid, AdminWebAuthnCredential> _webAuthnCredentials = new();
    private readonly ConcurrentDictionary<string, Guid> _webAuthnCredentialIds = new();
    private readonly ConcurrentDictionary<Guid, AdminUserPreferences> _preferences = new();

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

    public ValueTask<bool> AddWebAuthnCredentialAsync(
        AdminWebAuthnCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var key = ToCredentialKey(credential.CredentialId);
        lock (_sync)
        {
            if (_webAuthnCredentialIds.ContainsKey(key))
            {
                return ValueTask.FromResult(false);
            }

            _webAuthnCredentials[credential.Id] = Clone(credential);
            _webAuthnCredentialIds[key] = credential.Id;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<AdminWebAuthnCredential>> ListWebAuthnCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AdminWebAuthnCredential>>(
            _webAuthnCredentials.Values
                .Where(c => c.UserId == userId)
                .OrderBy(c => c.CreatedAt)
                .Select(Clone)
                .ToList());

    public ValueTask<AdminWebAuthnCredential?> GetWebAuthnCredentialByCredentialIdAsync(
        byte[] credentialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        var key = ToCredentialKey(credentialId);
        if (!_webAuthnCredentialIds.TryGetValue(key, out var id)
            || !_webAuthnCredentials.TryGetValue(id, out var credential))
        {
            return ValueTask.FromResult<AdminWebAuthnCredential?>(null);
        }

        return ValueTask.FromResult<AdminWebAuthnCredential?>(Clone(credential));
    }

    public ValueTask<bool> UpdateWebAuthnCredentialUsageAsync(
        Guid id,
        long signCount,
        DateTimeOffset lastUsedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_webAuthnCredentials.TryGetValue(id, out var credential))
            {
                return ValueTask.FromResult(false);
            }

            _webAuthnCredentials[id] = credential with
            {
                SignCount = signCount,
                LastUsedAt = lastUsedAt.ToUniversalTime(),
            };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> DeleteWebAuthnCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_webAuthnCredentials.TryGetValue(credentialId, out var credential)
                || credential.UserId != userId)
            {
                return ValueTask.FromResult(false);
            }

            _webAuthnCredentials.TryRemove(credentialId, out _);
            _webAuthnCredentialIds.TryRemove(ToCredentialKey(credential.CredentialId), out _);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<AdminUserPreferences> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_preferences.GetValueOrDefault(userId) ?? new AdminUserPreferences
        {
            UserId = userId,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    public ValueTask SavePreferencesAsync(AdminUserPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences[preferences.UserId] = preferences with
        {
            TimeZoneId = preferences.TimeZoneId.Trim(),
            PageSize = Math.Clamp(
                preferences.PageSize,
                AdminUserPreferences.MinPageSize,
                AdminUserPreferences.MaxPageSize),
            StatusRefreshSeconds = Math.Clamp(
                preferences.StatusRefreshSeconds,
                AdminUserPreferences.MinStatusRefreshSeconds,
                AdminUserPreferences.MaxStatusRefreshSeconds),
            UpdatedAt = preferences.UpdatedAt.ToUniversalTime(),
        };
        return ValueTask.CompletedTask;
    }

    private static string NormalizeUsername(string username) =>
        username.Trim().ToLowerInvariant();

    private static string ToCredentialKey(byte[] credentialId) =>
        WebAuthnBase64Url.Encode(credentialId);

    private static AdminWebAuthnCredential Clone(AdminWebAuthnCredential credential) => credential with
    {
        CredentialId = credential.CredentialId.ToArray(),
        PublicKey = credential.PublicKey.ToArray(),
    };
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

internal static class InMemoryPaging
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    public static IEnumerable<NormalizedEvent> ApplyFilter(
        IEnumerable<NormalizedEvent> source,
        EventListFilter? filter)
    {
        var text = ListFilterText.Normalize(filter?.Text);
        if (text is null)
        {
            return source;
        }

        var query = SearchQuery.Parse(text);
        if (query.IsEmpty)
        {
            return source;
        }

        return source.Where(item =>
            Matches(
                query,
                item.SourceId,
                JsonSerializer.Serialize<EventPayload>(item.Payload, JsonOptions)));
    }

    public static IEnumerable<Incident> ApplyFilter(
        IEnumerable<Incident> source,
        IncidentListFilter? filter)
    {
        var query = source;
        var text = ListFilterText.Normalize(filter?.Text);
        if (text is not null)
        {
            var search = SearchQuery.Parse(text);
            if (!search.IsEmpty)
            {
                query = query.Where(item => Matches(search, item.CorrelationKey));
            }
        }

        if (filter?.State is { } state && Enum.IsDefined(typeof(IncidentState), state))
        {
            query = query.Where(item => item.State == state);
        }

        return query;
    }

    public static IEnumerable<Decision> ApplyFilter(
        IEnumerable<Decision> source,
        DecisionListFilter? filter)
    {
        var query = source;
        var text = ListFilterText.Normalize(filter?.Text);
        if (text is not null)
        {
            var search = SearchQuery.Parse(text);
            if (!search.IsEmpty)
            {
                query = query.Where(item => Matches(search, item.Rationale));
            }
        }

        if (filter?.Outcome is { } outcome && Enum.IsDefined(typeof(DecisionOutcome), outcome))
        {
            query = query.Where(item => item.Outcome == outcome);
        }

        return query;
    }

    public static IEnumerable<AuditRecord> ApplyFilter(
        IEnumerable<AuditRecord> source,
        AuditListFilter? filter)
    {
        var query = source;
        var text = ListFilterText.Normalize(filter?.Text);
        if (text is not null)
        {
            var search = SearchQuery.Parse(text);
            if (!search.IsEmpty)
            {
                query = query.Where(item => Matches(search, item.Summary));
            }
        }

        if (filter?.Stage is { } stage && Enum.IsDefined(typeof(PipelineStage), stage))
        {
            query = query.Where(item => item.Stage == stage);
        }

        return query;
    }

    public static KeysetPage<T> Page<T>(
        IEnumerable<T> source,
        Guid? beforeId,
        int pageSize,
        Func<T, Guid> getId,
        Comparison<T>? comparison = null)
    {
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var ordered = source.ToList();
        ordered.Sort(comparison ?? CompareBy(getId, getId, SortDirection.Desc));
        var totalCount = ordered.Count;
        var startIndex = 0;
        if (beforeId is not null)
        {
            var cursorIndex = ordered.FindIndex(item => getId(item) == beforeId.Value);
            startIndex = cursorIndex < 0 ? ordered.Count : cursorIndex + 1;
        }

        var pagePlusOne = ordered
            .Skip(startIndex)
            .Take(safePageSize + 1)
            .ToList();
        var items = pagePlusOne.Take(safePageSize).ToList();
        var nextCursor = pagePlusOne.Count > safePageSize && items.Count > 0
            ? getId(items[^1])
            : (Guid?)null;
        var preceding = items.Count == 0
            ? 0
            : ordered.FindIndex(item => getId(item) == getId(items[0]));
        return new KeysetPage<T>(items, nextCursor, totalCount, preceding);
    }

    public static Guid? PageCursor<T>(
        IEnumerable<T> source,
        int pageNumber,
        int pageSize,
        Func<T, Guid> getId,
        Comparison<T>? comparison = null)
    {
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var ordered = source.ToList();
        ordered.Sort(comparison ?? CompareBy(getId, getId, SortDirection.Desc));
        var totalPages = Math.Max(1, (long)Math.Ceiling(ordered.Count / (double)safePageSize));
        var safePageNumber = Math.Clamp((long)pageNumber, 1, totalPages);
        if (safePageNumber <= 1)
        {
            return null;
        }

        var boundaryIndex = (safePageNumber - 1) * safePageSize - 1;
        return boundaryIndex >= 0 && boundaryIndex < ordered.Count
            ? getId(ordered[(int)boundaryIndex])
            : null;
    }

    private static bool Matches(SearchQuery query, params string[] values) =>
        query.Groups.Any(group =>
            group.Include.All(term => values.Any(value => Contains(value, term)))
            && group.Exclude.All(term => values.All(value => !Contains(value, term))));

    private static bool Contains(string value, string filter) =>
        value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public static Comparison<NormalizedEvent>? EventComparison(ListSort<EventSortColumn>? sort)
    {
        if (sort is not { } active || !ValidDirection(active.Direction))
        {
            return null;
        }

        return active.Column switch
        {
            EventSortColumn.Occurred => CompareBy<NormalizedEvent, DateTimeOffset>(e => e.OccurredAt, e => e.Id, active.Direction),
            EventSortColumn.Source => CompareBy<NormalizedEvent, string>(e => e.SourceId, e => e.Id, active.Direction, StringComparer.Ordinal),
            _ => null,
        };
    }

    public static Comparison<Incident>? IncidentComparison(ListSort<IncidentSortColumn>? sort)
    {
        if (sort is not { } active || !ValidDirection(active.Direction))
        {
            return null;
        }

        return active.Column switch
        {
            IncidentSortColumn.CorrelationKey => CompareBy<Incident, string>(i => i.CorrelationKey, i => i.Id, active.Direction, StringComparer.Ordinal),
            IncidentSortColumn.Window => CompareBy<Incident, DateTimeOffset>(i => i.WindowStart, i => i.Id, active.Direction),
            IncidentSortColumn.State => CompareBy<Incident, int>(i => (int)i.State, i => i.Id, active.Direction),
            _ => null,
        };
    }

    public static Comparison<Decision>? DecisionComparison(ListSort<DecisionSortColumn>? sort)
    {
        if (sort is not { } active || !ValidDirection(active.Direction))
        {
            return null;
        }

        return active.Column switch
        {
            DecisionSortColumn.Created => CompareBy<Decision, DateTimeOffset>(d => d.CreatedAt, d => d.Id, active.Direction),
            DecisionSortColumn.Policy => CompareBy<Decision, string>(d => d.PolicyId, d => d.Id, active.Direction, StringComparer.Ordinal),
            DecisionSortColumn.Outcome => CompareBy<Decision, int>(d => (int)d.Outcome, d => d.Id, active.Direction),
            DecisionSortColumn.Classification => CompareBy<Decision, Guid>(d => d.ClassificationId, d => d.Id, active.Direction),
            _ => null,
        };
    }

    public static Comparison<AuditRecord>? AuditComparison(ListSort<AuditSortColumn>? sort)
    {
        if (sort is not { } active || !ValidDirection(active.Direction))
        {
            return null;
        }

        return active.Column switch
        {
            AuditSortColumn.Timestamp => CompareBy<AuditRecord, DateTimeOffset>(r => r.Timestamp, r => r.Id, active.Direction),
            AuditSortColumn.Stage => CompareBy<AuditRecord, int>(r => (int)r.Stage, r => r.Id, active.Direction),
            AuditSortColumn.Source => CompareBy<AuditRecord, string>(r => r.SourceId ?? string.Empty, r => r.Id, active.Direction, StringComparer.Ordinal),
            _ => null,
        };
    }

    public static Comparison<CustomSignature>? SignatureComparison(ListSort<SignatureSortColumn>? sort)
    {
        if (sort is not { } active || !ValidDirection(active.Direction))
        {
            return null;
        }

        return active.Column switch
        {
            SignatureSortColumn.Name => CompareBy<CustomSignature, string>(s => s.Name, s => s.Id, active.Direction, StringComparer.Ordinal),
            SignatureSortColumn.Target => CompareBy<CustomSignature, int>(s => (int)s.Target, s => s.Id, active.Direction),
            SignatureSortColumn.Match => CompareBy<CustomSignature, int>(s => (int)s.MatchType, s => s.Id, active.Direction),
            SignatureSortColumn.Category => CompareBy<CustomSignature, string>(s => s.Category, s => s.Id, active.Direction, StringComparer.Ordinal),
            SignatureSortColumn.Severity => CompareBy<CustomSignature, int>(s => s.Severity, s => s.Id, active.Direction),
            SignatureSortColumn.Enabled => CompareBy<CustomSignature, bool>(s => s.Enabled, s => s.Id, active.Direction),
            SignatureSortColumn.Updated => CompareBy<CustomSignature, DateTimeOffset>(s => s.UpdatedAt, s => s.Id, active.Direction),
            SignatureSortColumn.Version => CompareBy<CustomSignature, int>(s => s.Version, s => s.Id, active.Direction),
            _ => null,
        };
    }

    internal static Comparison<T> CompareBy<T, TKey>(
        Func<T, TKey> getKey,
        Func<T, Guid> getId,
        SortDirection direction,
        IComparer<TKey>? comparer = null)
    {
        var keyComparer = comparer ?? Comparer<TKey>.Default;
        return (left, right) =>
        {
            var result = keyComparer.Compare(getKey(left), getKey(right));
            if (result == 0)
            {
                result = getId(left).CompareTo(getId(right));
            }

            return direction == SortDirection.Asc ? result : -result;
        };
    }

    private static bool ValidDirection(SortDirection direction) =>
        Enum.IsDefined(typeof(SortDirection), direction);
}
