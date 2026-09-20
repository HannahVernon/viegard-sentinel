using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Feedback;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Stores;

/// <summary>Persistence port for raw observations (Roost).</summary>
public interface IRawObservationStore
{
    /// <summary>
    /// Stores the observation.  Returns false when an observation with the
    /// same payload reference already exists (at-least-once redelivery after
    /// an unclean stop); callers treat that as already ingested.
    /// </summary>
    ValueTask<bool> AddAsync(RawObservation observation, string rawPayload, CancellationToken cancellationToken = default);

    ValueTask<RawObservation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Retrieve the stored raw payload by its reference.</summary>
    ValueTask<string?> GetPayloadAsync(string payloadReference, CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for normalized events (Roost).</summary>
public interface IEventStore
{
    ValueTask AddAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken = default);

    ValueTask<NormalizedEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<Guid, NormalizedEvent>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    ValueTask<KeysetPage<NormalizedEvent>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        EventListFilter? filter = null,
        ListSort<EventSortColumn>? sort = null,
        CancellationToken cancellationToken = default);

    ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        EventListFilter? filter = null,
        ListSort<EventSortColumn>? sort = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for incidents (Roost).</summary>
public interface IIncidentStore
{
    ValueTask UpsertAsync(Incident incident, CancellationToken cancellationToken = default);

    ValueTask<Incident?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<Guid, Incident>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    ValueTask<Incident?> FindOpenByCorrelationKeyAsync(string correlationKey, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Incident>> FindByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default);

    ValueTask<KeysetPage<Incident>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        IncidentListFilter? filter = null,
        ListSort<IncidentSortColumn>? sort = null,
        CancellationToken cancellationToken = default);

    ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        IncidentListFilter? filter = null,
        ListSort<IncidentSortColumn>? sort = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for classifications (Roost).</summary>
public interface IClassificationStore
{
    ValueTask AddAsync(Classification classification, CancellationToken cancellationToken = default);

    ValueTask<Classification?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<Guid, Classification>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Classification>> ListForSubjectAsync(
        ClassificationSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken = default);

    ValueTask<KeysetPage<Classification>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for policy decisions (Roost).</summary>
public interface IDecisionStore
{
    ValueTask AddAsync(Decision decision, CancellationToken cancellationToken = default);

    ValueTask<Decision?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Decision>> ListForClassificationAsync(
        Guid classificationId,
        CancellationToken cancellationToken = default);

    ValueTask<KeysetPage<Decision>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        DecisionListFilter? filter = null,
        ListSort<DecisionSortColumn>? sort = null,
        CancellationToken cancellationToken = default);

    ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        DecisionListFilter? filter = null,
        ListSort<DecisionSortColumn>? sort = null,
        CancellationToken cancellationToken = default);

    ValueTask<Decision?> TryReviewAsync(
        Guid id,
        DecisionReviewOutcome outcome,
        string reviewedBy,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects every unreviewed RequireApproval decision whose
    /// classification severity is at or below <paramref name="maxSeverity"/>
    /// and returns the number rejected.  Intended for clearing low-severity
    /// review-queue noise in one audited action.
    /// </summary>
    ValueTask<int> BulkRejectUnreviewedAsync(
        int maxSeverity,
        string reviewedBy,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the unreviewed RequireApproval decisions whose classification
    /// severity is at or below <paramref name="maxSeverity"/>: the set a
    /// bulk reject at that severity would claim.
    /// </summary>
    ValueTask<int> CountUnreviewedAtOrBelowAsync(
        int maxSeverity,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for action records (Roost).</summary>
public interface IActionStore
{
    ValueTask AddAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default);

    ValueTask<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ActionRecord>> ListRecentByProviderAsync(
        string providerId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for human corrections (Roost).</summary>
public interface ICorrectionStore
{
    ValueTask AddAsync(Correction correction, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Correction>> GetForClassificationAsync(Guid classificationId, CancellationToken cancellationToken = default);
}
