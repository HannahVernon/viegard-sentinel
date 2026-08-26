using Microsoft.EntityFrameworkCore;
using Viegard.Application.Audit;
using Viegard.Application.Stores;
using Viegard.Domain.Admin;
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
