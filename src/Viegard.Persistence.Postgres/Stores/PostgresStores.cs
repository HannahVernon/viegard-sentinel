using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viegard.Application.Audit;
using Viegard.Application.Stores;
using Viegard.Domain.Admin;
using Viegard.Application.Telemetry;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Configuration;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Feedback;
using Viegard.Domain.Health;
using Viegard.Domain.Incidents;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres.Stores;

/// <summary>PostgreSQL implementations of the persistence ports (D-0024).</summary>
public sealed class PostgresRawObservationStore(IDbContextFactory<ViegardDbContext> factory, ReferenceResolver resolver) : IRawObservationStore
{
    public async ValueTask AddAsync(RawObservation observation, string rawPayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var sourceRef = await resolver.ResolveSourceAsync(observation.SourceId, observation.SourceType, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.RawObservations.Add(observation.ToRow(rawPayload, sourceRef));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RawObservation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.RawObservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var (sourceKey, sourceType) = await resolver.GetSourceAsync(row.SourceId, cancellationToken).ConfigureAwait(false);
        // A null type means no typed writer has ever been seen for this
        // source key; label the absence rather than guess.
        return row.ToDomain(sourceKey, sourceType ?? "unknown");
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

public sealed class PostgresEventStore(IDbContextFactory<ViegardDbContext> factory, ReferenceResolver resolver) : IEventStore
{
    public async ValueTask AddAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);
        var sourceRef = await resolver.ResolveSourceAsync(normalizedEvent.SourceId, normalizedEvent.SourceType, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Events.Add(normalizedEvent.ToRow(sourceRef));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NormalizedEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Events.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var (sourceKey, sourceType) = await resolver.GetSourceAsync(row.SourceId, cancellationToken).ConfigureAwait(false);
        return row.ToDomain(sourceKey, sourceType ?? "unknown");
    }

    public async ValueTask<KeysetPage<NormalizedEvent>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePageSize = PostgresPaging.SafePageSize(pageSize);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var take = safePageSize + 1;
        var rows = await (beforeId is null
            ? db.Events.FromSqlInterpolated($"SELECT * FROM events ORDER BY id DESC LIMIT {take}")
            : db.Events.FromSqlInterpolated($"SELECT * FROM events WHERE id < {beforeId.Value} ORDER BY id DESC LIMIT {take}"))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var domains = new List<NormalizedEvent>();
        foreach (var row in rows.Take(safePageSize))
        {
            var (sourceKey, sourceType) = await resolver.GetSourceAsync(row.SourceId, cancellationToken).ConfigureAwait(false);
            domains.Add(row.ToDomain(sourceKey, sourceType ?? "unknown"));
        }

        var nextCursor = rows.Count > safePageSize && domains.Count > 0 ? domains[^1].Id : (Guid?)null;
        // COUNT(*) is acceptable for these operator-only admin lists at the
        // expected scale, and keeps the page indicator honest.
        var totalCount = await db.Events.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var preceding = domains.Count == 0
            ? 0
            : await db.Events.LongCountAsync(e => e.Id.CompareTo(domains[0].Id) > 0, cancellationToken).ConfigureAwait(false);
        return new KeysetPage<NormalizedEvent>(domains, nextCursor, totalCount, preceding);
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

    public async ValueTask<KeysetPage<Incident>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePageSize = PostgresPaging.SafePageSize(pageSize);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var take = safePageSize + 1;
        var rows = await (beforeId is null
            ? db.Incidents.FromSqlInterpolated($"SELECT * FROM incidents ORDER BY id DESC LIMIT {take}")
            : db.Incidents.FromSqlInterpolated($"SELECT * FROM incidents WHERE id < {beforeId.Value} ORDER BY id DESC LIMIT {take}"))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = rows.Take(safePageSize).Select(r => r.ToDomain()).ToList();
        var nextCursor = rows.Count > safePageSize && items.Count > 0 ? items[^1].Id : (Guid?)null;
        // COUNT(*) is acceptable for these operator-only admin lists at the
        // expected scale, and keeps the page indicator honest.
        var totalCount = await db.Incidents.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var preceding = items.Count == 0
            ? 0
            : await db.Incidents.LongCountAsync(i => i.Id.CompareTo(items[0].Id) > 0, cancellationToken).ConfigureAwait(false);
        return new KeysetPage<Incident>(items, nextCursor, totalCount, preceding);
    }
}

public sealed class PostgresClassificationStore(IDbContextFactory<ViegardDbContext> factory, ReferenceResolver resolver) : IClassificationStore
{
    public async ValueTask AddAsync(Classification classification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        var classifierRef = await resolver.ResolveClassifierAsync(classification.ClassifierId, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Classifications.Add(classification.ToRow(classifierRef));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Classification?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Classifications.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var classifierKey = await resolver.GetClassifierAsync(row.ClassifierId, cancellationToken).ConfigureAwait(false);
        return row.ToDomain(classifierKey);
    }

    public async ValueTask<IReadOnlyList<Classification>> ListForSubjectAsync(
        ClassificationSubjectKind subjectKind,
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Classifications.AsNoTracking()
            .Where(r => r.SubjectKind == (int)subjectKind && r.SubjectId == subjectId)
            .OrderByDescending(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = new List<Classification>();
        foreach (var row in rows)
        {
            var classifierKey = await resolver.GetClassifierAsync(row.ClassifierId, cancellationToken).ConfigureAwait(false);
            items.Add(row.ToDomain(classifierKey));
        }

        return items;
    }

    public async ValueTask<KeysetPage<Classification>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePageSize = PostgresPaging.SafePageSize(pageSize);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var take = safePageSize + 1;
        var rows = await (beforeId is null
            ? db.Classifications.FromSqlInterpolated($"SELECT * FROM classifications ORDER BY id DESC LIMIT {take}")
            : db.Classifications.FromSqlInterpolated($"SELECT * FROM classifications WHERE id < {beforeId.Value} ORDER BY id DESC LIMIT {take}"))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = new List<Classification>();
        foreach (var row in rows.Take(safePageSize))
        {
            var classifierKey = await resolver.GetClassifierAsync(row.ClassifierId, cancellationToken).ConfigureAwait(false);
            items.Add(row.ToDomain(classifierKey));
        }

        var nextCursor = rows.Count > safePageSize && items.Count > 0 ? items[^1].Id : (Guid?)null;
        // COUNT(*) is acceptable for these operator-only admin lists at the
        // expected scale, and keeps the page indicator honest.
        var totalCount = await db.Classifications.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var preceding = items.Count == 0
            ? 0
            : await db.Classifications.LongCountAsync(c => c.Id.CompareTo(items[0].Id) > 0, cancellationToken).ConfigureAwait(false);
        return new KeysetPage<Classification>(items, nextCursor, totalCount, preceding);
    }
}

public sealed class PostgresDecisionStore(IDbContextFactory<ViegardDbContext> factory, ReferenceResolver resolver) : IDecisionStore
{
    public async ValueTask AddAsync(Decision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var policyRef = await resolver.ResolvePolicyAsync(decision.PolicyId, decision.PolicyVersion, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Decisions.Add(decision.ToRow(policyRef));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Decision?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Decisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var (policyKey, policyVersion) = await resolver.GetPolicyAsync(row.PolicyId, cancellationToken).ConfigureAwait(false);
        return row.ToDomain(policyKey, policyVersion);
    }

    public async ValueTask<IReadOnlyList<Decision>> ListForClassificationAsync(
        Guid classificationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Decisions.AsNoTracking()
            .Where(r => r.ClassificationId == classificationId)
            .OrderByDescending(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = new List<Decision>();
        foreach (var row in rows)
        {
            var (policyKey, policyVersion) = await resolver.GetPolicyAsync(row.PolicyId, cancellationToken).ConfigureAwait(false);
            items.Add(row.ToDomain(policyKey, policyVersion));
        }

        return items;
    }

    public async ValueTask<KeysetPage<Decision>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePageSize = PostgresPaging.SafePageSize(pageSize);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var take = safePageSize + 1;
        var rows = await (beforeId is null
            ? db.Decisions.FromSqlInterpolated($"SELECT * FROM decisions ORDER BY id DESC LIMIT {take}")
            : db.Decisions.FromSqlInterpolated($"SELECT * FROM decisions WHERE id < {beforeId.Value} ORDER BY id DESC LIMIT {take}"))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = new List<Decision>();
        foreach (var row in rows.Take(safePageSize))
        {
            var (policyKey, policyVersion) = await resolver.GetPolicyAsync(row.PolicyId, cancellationToken).ConfigureAwait(false);
            items.Add(row.ToDomain(policyKey, policyVersion));
        }

        var nextCursor = rows.Count > safePageSize && items.Count > 0 ? items[^1].Id : (Guid?)null;
        // COUNT(*) is acceptable for these operator-only admin lists at the
        // expected scale, and keeps the page indicator honest.
        var totalCount = await db.Decisions.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var preceding = items.Count == 0
            ? 0
            : await db.Decisions.LongCountAsync(d => d.Id.CompareTo(items[0].Id) > 0, cancellationToken).ConfigureAwait(false);
        return new KeysetPage<Decision>(items, nextCursor, totalCount, preceding);
    }
}

public sealed class PostgresActionStore(IDbContextFactory<ViegardDbContext> factory, ReferenceResolver resolver) : IActionStore
{
    public async ValueTask UpsertAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionRecord);
        var providerRef = await resolver.ResolveActionProviderAsync(actionRecord.ProviderId, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Actions.FirstOrDefaultAsync(r => r.Id == actionRecord.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.Actions.Add(actionRecord.ToRow(providerRef));
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(actionRecord.ToRow(providerRef));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Actions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var providerKey = await resolver.GetActionProviderAsync(row.ProviderId, cancellationToken).ConfigureAwait(false);
        return row.ToDomain(providerKey);
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
public sealed class PostgresAuditLedger(IDbContextFactory<ViegardDbContext> factory, ReferenceResolver resolver) : IAuditLedger
{
    public async ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        // The audit path does not know the source type; the resolver only
        // creates a row when the key has never been seen by a typed writer.
        var sourceRef = record.SourceId is null
            ? (int?)null
            : await resolver.ResolveSourceAsync(record.SourceId, sourceType: null, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AuditRecords.Add(record.ToRow(sourceRef));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<KeysetPage<AuditRecord>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePageSize = PostgresPaging.SafePageSize(pageSize);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var take = safePageSize + 1;
        var rows = await (beforeId is null
            ? db.AuditRecords.FromSqlInterpolated($"SELECT * FROM audit_records ORDER BY id DESC LIMIT {take}")
            : db.AuditRecords.FromSqlInterpolated($"SELECT * FROM audit_records WHERE id < {beforeId.Value} ORDER BY id DESC LIMIT {take}"))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = new List<AuditRecord>();
        foreach (var row in rows.Take(safePageSize))
        {
            string? sourceKey = null;
            if (row.SourceId is not null)
            {
                var resolved = await resolver.GetSourceAsync(row.SourceId.Value, cancellationToken).ConfigureAwait(false);
                sourceKey = resolved.Key;
            }

            items.Add(row.ToDomain(sourceKey));
        }

        var nextCursor = rows.Count > safePageSize && items.Count > 0 ? items[^1].Id : (Guid?)null;
        // COUNT(*) is acceptable for these operator-only admin lists at the
        // expected scale, and keeps the page indicator honest.
        var totalCount = await db.AuditRecords.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var preceding = items.Count == 0
            ? 0
            : await db.AuditRecords.LongCountAsync(a => a.Id.CompareTo(items[0].Id) > 0, cancellationToken).ConfigureAwait(false);
        return new KeysetPage<AuditRecord>(items, nextCursor, totalCount, preceding);
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

public sealed class PostgresCustomSignatureStore(IDbContextFactory<ViegardDbContext> factory, NpgsqlDataSource dataSource)
    : ICustomSignatureStore
{
    private const string NotifyChannel = "viegard_config_signatures";
    private const string UniqueViolation = "23505";
    private long _changeVersion;
    private int _seedChecked;

    public long CurrentChangeVersion => Volatile.Read(ref _changeVersion);

    public async ValueTask<IReadOnlyList<CustomSignature>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.CustomSignatures.AsNoTracking()
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async ValueTask<CustomSignature?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.CustomSignatures.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask<CustomSignature> UpsertAsync(
        CustomSignature signature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        var normalized = CustomSignatureValidator.Normalize(signature);
        var validation = CustomSignatureValidator.Validate(normalized);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(CustomSignatureValidator.UniformError(validation));
        }

        var now = DateTimeOffset.UtcNow;
        CustomSignature saved;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.CustomSignatures
            .FirstOrDefaultAsync(s => s.Id == normalized.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            saved = normalized with
            {
                CreatedAt = normalized.CreatedAt == default ? now : normalized.CreatedAt.ToUniversalTime(),
                UpdatedAt = now,
                Version = 1,
            };
            db.CustomSignatures.Add(saved.ToRow());
        }
        else
        {
            saved = normalized with
            {
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
                Version = existing.Version + 1,
            };
            db.Entry(existing).CurrentValues.SetValues(saved.ToRow());
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new InvalidOperationException("A signature with that name already exists.", ex);
        }

        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async ValueTask<CustomSignature?> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.CustomSignatures.FirstOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var removed = existing.ToDomain();
        db.CustomSignatures.Remove(existing);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var current = CurrentChangeVersion;
        if (current != lastSeenVersion)
        {
            return current;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var notified = false;
            connection.Notification += (_, _) => notified = true;
            await using (var listen = connection.CreateCommand())
            {
                listen.CommandText = $"LISTEN {NotifyChannel};";
                await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await connection.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return notified ? Interlocked.Increment(ref _changeVersion) : CurrentChangeVersion;
        }
        catch (NpgsqlException)
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            return CurrentChangeVersion;
        }
    }

    private async ValueTask EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _seedChecked) == 1)
        {
            return;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.CustomSignatures.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _seedChecked, 1);
            return;
        }

        var seed = CustomSignatureSeeds.AftershipReferralBot(DateTimeOffset.UtcNow);
        db.CustomSignatures.Add(seed.ToRow());
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await NotifyChangedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.ChangeTracker.Clear();
        }

        Volatile.Write(ref _seedChecked, 1);
    }

    private async ValueTask NotifyChangedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_notify('{NotifyChannel}', '');";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _changeVersion);
    }
}

public sealed class PostgresAdminUserStore(IDbContextFactory<ViegardDbContext> factory) : IAdminUserStore
{
    public async ValueTask<bool> AnyUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.AdminUsers.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AdminUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask<AdminUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUsername(username);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AdminUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == normalized, cancellationToken)
            .ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask CreateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AdminUsers.Add((user with { Username = NormalizeUsername(user.Username) }).ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.AdminUsers.FirstAsync(u => u.Id == user.Id, cancellationToken).ConfigureAwait(false);
        db.Entry(existing).CurrentValues.SetValues((user with { Username = NormalizeUsername(user.Username) }).ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AdminTotpSecret?> GetTotpSecretAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AdminTotpSecrets.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask UpsertTotpSecretAsync(AdminTotpSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.AdminTotpSecrets.FirstOrDefaultAsync(s => s.UserId == secret.UserId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.AdminTotpSecrets.Add(secret.ToRow());
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(secret.ToRow());
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> TrySetTotpLastAcceptedStepAsync(
        Guid userId,
        long acceptedStep,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.AdminTotpSecrets
            .Where(s => s.UserId == userId && (s.LastAcceptedStep == null || s.LastAcceptedStep < acceptedStep))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastAcceptedStep, acceptedStep), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    public async ValueTask<IReadOnlyList<AdminRecoveryCode>> GetRecoveryCodesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AdminRecoveryCodes.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async ValueTask ReplaceRecoveryCodesAsync(
        Guid userId,
        IReadOnlyList<AdminRecoveryCode> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.AdminRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.AdminRecoveryCodes.AddRange(codes.Select(c => c.ToRow()));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> TryMarkRecoveryCodeUsedAsync(
        Guid codeId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.AdminRecoveryCodes
            .Where(c => c.Id == codeId && c.UsedAt == null)
            .ExecuteUpdateAsync(c => c.SetProperty(r => r.UsedAt, usedAt.ToUniversalTime()), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    public async ValueTask<bool> AddWebAuthnCredentialAsync(
        AdminWebAuthnCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.AdminWebAuthnCredentials.AsNoTracking()
            .AnyAsync(c => c.CredentialId.SequenceEqual(credential.CredentialId), cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        db.AdminWebAuthnCredentials.Add(credential.ToRow());
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async ValueTask<IReadOnlyList<AdminWebAuthnCredential>> ListWebAuthnCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AdminWebAuthnCredentials.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async ValueTask<AdminWebAuthnCredential?> GetWebAuthnCredentialByCredentialIdAsync(
        byte[] credentialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AdminWebAuthnCredentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CredentialId.SequenceEqual(credentialId), cancellationToken)
            .ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask<bool> UpdateWebAuthnCredentialUsageAsync(
        Guid id,
        long signCount,
        DateTimeOffset lastUsedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.AdminWebAuthnCredentials
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(c => c
                .SetProperty(r => r.SignCount, signCount)
                .SetProperty(r => r.LastUsedAt, lastUsedAt.ToUniversalTime()), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    public async ValueTask<bool> DeleteWebAuthnCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await db.AdminWebAuthnCredentials
            .Where(c => c.UserId == userId && c.Id == credentialId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted == 1;
    }

    public async ValueTask<AdminUserPreferences> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AdminUserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        return row?.ToDomain() ?? new AdminUserPreferences
        {
            UserId = userId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public async ValueTask SavePreferencesAsync(
        AdminUserPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences with
        {
            TimeZoneId = preferences.TimeZoneId.Trim(),
            PageSize = Math.Clamp(
                preferences.PageSize,
                AdminUserPreferences.MinPageSize,
                AdminUserPreferences.MaxPageSize),
            UpdatedAt = preferences.UpdatedAt.ToUniversalTime(),
        };

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.AdminUserPreferences
            .FirstOrDefaultAsync(p => p.UserId == normalized.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            db.AdminUserPreferences.Add(normalized.ToRow());
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(normalized.ToRow());
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeUsername(string username) =>
        username.Trim().ToLowerInvariant();
}

public sealed class PostgresAdminSessionStore(IDbContextFactory<ViegardDbContext> factory) : IAdminSessionStore
{
    public async ValueTask CreateAsync(AdminSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AdminSessions.Add(session.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AdminSession?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AdminSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async ValueTask<IReadOnlyList<AdminSession>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AdminSessions.AsNoTracking()
            .Where(s => s.UserId == userId
                && s.RevokedAt == null
                && s.AbsoluteExpiresAt > now
                && s.IdleExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async ValueTask UpdateActivityAsync(
        Guid id,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.AdminSessions
            .Where(s => s.Id == id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LastSeenAt, lastSeenAt.ToUniversalTime())
                .SetProperty(r => r.IdleExpiresAt, idleExpiresAt.ToUniversalTime()), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask StampStepUpAsync(Guid id, DateTimeOffset stepUpAt, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.AdminSessions
            .Where(s => s.Id == id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.StepUpAt, stepUpAt.ToUniversalTime()), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.AdminSessions
            .Where(s => s.Id == id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, revokedAt.ToUniversalTime()), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RevokeForUserAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        Guid? exceptSessionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.AdminSessions.Where(s => s.UserId == userId && s.RevokedAt == null);
        if (exceptSessionId is not null)
        {
            query = query.Where(s => s.Id != exceptSessionId.Value);
        }

        await query.ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, revokedAt.ToUniversalTime()), cancellationToken)
            .ConfigureAwait(false);
    }
}

internal static class PostgresPaging
{
    public static int SafePageSize(int pageSize) => Math.Clamp(pageSize, 1, 200);
}
