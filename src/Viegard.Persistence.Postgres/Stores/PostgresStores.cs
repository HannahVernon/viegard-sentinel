using Microsoft.EntityFrameworkCore;
using Viegard.Application.Audit;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Feedback;
using Viegard.Domain.Health;
using Viegard.Domain.Incidents;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres.Stores;

/// <summary>PostgreSQL implementations of the persistence ports (D-0024).</summary>
public sealed class PostgresRawObservationStore(IDbContextFactory<ViegardDbContext> factory) : IRawObservationStore
{
    public async ValueTask AddAsync(RawObservation observation, string rawPayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.RawObservations.Add(observation.ToRow(rawPayload));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RawObservation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.RawObservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask<string?> GetPayloadAsync(string payloadReference, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.RawObservations.AsNoTracking()
            .Where(r => r.PayloadReference == payloadReference)
            .Select(r => r.RawPayload)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgresEventStore(IDbContextFactory<ViegardDbContext> factory) : IEventStore
{
    public async ValueTask AddAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Events.Add(normalizedEvent.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NormalizedEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Events.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }
}

public sealed class PostgresIncidentStore(IDbContextFactory<ViegardDbContext> factory) : IIncidentStore
{
    public async ValueTask UpsertAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Incidents.FirstOrDefaultAsync(r => r.Id == incident.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.Incidents.Add(incident.ToRow());
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(incident.ToRow());
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Incident?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask<Incident?> FindOpenByCorrelationKeyAsync(string correlationKey, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Incidents.AsNoTracking()
            .Where(r => r.CorrelationKey == correlationKey && r.State == (int)IncidentState.Open)
            .OrderByDescending(r => r.WindowEnd)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }
}

public sealed class PostgresClassificationStore(IDbContextFactory<ViegardDbContext> factory) : IClassificationStore
{
    public async ValueTask AddAsync(Classification classification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Classifications.Add(classification.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Classification?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Classifications.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }
}

public sealed class PostgresDecisionStore(IDbContextFactory<ViegardDbContext> factory) : IDecisionStore
{
    public async ValueTask AddAsync(Decision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Decisions.Add(decision.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Decision?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Decisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }
}

public sealed class PostgresActionStore(IDbContextFactory<ViegardDbContext> factory) : IActionStore
{
    public async ValueTask UpsertAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionRecord);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Actions.FirstOrDefaultAsync(r => r.Id == actionRecord.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.Actions.Add(actionRecord.ToRow());
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(actionRecord.ToRow());
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Actions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }
}

public sealed class PostgresCorrectionStore(IDbContextFactory<ViegardDbContext> factory) : ICorrectionStore
{
    public async ValueTask AddAsync(Correction correction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correction);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Corrections.Add(correction.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<Correction>> GetForClassificationAsync(Guid classificationId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Corrections.AsNoTracking()
            .Where(r => r.ClassificationId == classificationId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }
}

/// <summary>Append-only audit ledger over PostgreSQL.</summary>
public sealed class PostgresAuditLedger(IDbContextFactory<ViegardDbContext> factory) : IAuditLedger
{
    public async ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AuditRecords.Add(record.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgresQueueTelemetryStore(IDbContextFactory<ViegardDbContext> factory) : IQueueTelemetryStore
{
    public async ValueTask PublishAsync(QueueTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.QueueTelemetry
            .FirstOrDefaultAsync(r => r.InstanceId == snapshot.InstanceId && r.QueueName == snapshot.QueueName, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            db.QueueTelemetry.Add(snapshot.ToRow());
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(snapshot.ToRow());
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<QueueTelemetrySnapshot>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.QueueTelemetry.AsNoTracking()
            .OrderBy(r => r.InstanceId).ThenBy(r => r.QueueName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }
}

public sealed class PostgresSourceOffsetStore(IDbContextFactory<ViegardDbContext> factory) : ISourceOffsetStore
{
    public async ValueTask<string?> GetAsync(string sourceId, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SourceOffsets.AsNoTracking()
            .Where(r => r.SourceId == sourceId && r.Key == key)
            .Select(r => r.Value)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(string sourceId, string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.SourceOffsets
            .FirstOrDefaultAsync(r => r.SourceId == sourceId && r.Key == key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.SourceOffsets.Add(new SourceOffsetRow { SourceId = sourceId, Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
