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
    ValueTask AddAsync(RawObservation observation, string rawPayload, CancellationToken cancellationToken = default);

    ValueTask<RawObservation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Retrieve the stored raw payload by its reference.</summary>
    ValueTask<string?> GetPayloadAsync(string payloadReference, CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for normalized events (Roost).</summary>
public interface IEventStore
{
    ValueTask AddAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken = default);

    ValueTask<NormalizedEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for incidents (Roost).</summary>
public interface IIncidentStore
{
    ValueTask UpsertAsync(Incident incident, CancellationToken cancellationToken = default);

    ValueTask<Incident?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<Incident?> FindOpenByCorrelationKeyAsync(string correlationKey, CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for classifications (Roost).</summary>
public interface IClassificationStore
{
    ValueTask AddAsync(Classification classification, CancellationToken cancellationToken = default);

    ValueTask<Classification?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for policy decisions (Roost).</summary>
public interface IDecisionStore
{
    ValueTask AddAsync(Decision decision, CancellationToken cancellationToken = default);

    ValueTask<Decision?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for action records (Roost).</summary>
public interface IActionStore
{
    ValueTask UpsertAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default);

    ValueTask<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for human corrections (Roost).</summary>
public interface ICorrectionStore
{
    ValueTask AddAsync(Correction correction, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Correction>> GetForClassificationAsync(Guid classificationId, CancellationToken cancellationToken = default);
}
