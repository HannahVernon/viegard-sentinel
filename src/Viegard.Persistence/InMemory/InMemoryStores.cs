using System.Collections.Concurrent;
using Viegard.Application.Audit;
using Viegard.Application.Stores;
using Viegard.Domain.Actions;
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
