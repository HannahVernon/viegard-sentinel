using Microsoft.EntityFrameworkCore;
using Viegard.Application.Retention;
using Viegard.Domain.Incidents;

namespace Viegard.Persistence.Postgres.Stores;

/// <summary>PostgreSQL batched retention deletes for high-volume tables.</summary>
public sealed class PostgresRetentionStore(IDbContextFactory<ViegardDbContext> factory) : IRetentionStore
{
    private static readonly TimeSpan InterBatchDelay = TimeSpan.FromMilliseconds(25);

    public async ValueTask<long> PurgeAsync(
        RetentionTarget target,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var totalDeleted = 0L;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await ExecuteBatchAsync(db, target, cutoff.ToUniversalTime(), batchSize, cancellationToken)
                .ConfigureAwait(false);
            totalDeleted += deleted;

            if (deleted < batchSize)
            {
                return totalDeleted;
            }

            await Task.Delay(InterBatchDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<int> ExecuteBatchAsync(
        ViegardDbContext db,
        RetentionTarget target,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken) => target switch
    {
        RetentionTarget.RawObservations => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM raw_observations
            WHERE ctid IN (
                SELECT ctid
                FROM raw_observations
                WHERE observed_at < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.Events => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM events
            WHERE ctid IN (
                SELECT ctid
                FROM events
                WHERE occurred_at < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.Incidents => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM incidents
            WHERE ctid IN (
                SELECT ctid
                FROM incidents
                WHERE window_start < {cutoff}
                  AND state <> {(int)IncidentState.Open}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.Classifications => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM classifications
            WHERE ctid IN (
                SELECT ctid
                FROM classifications
                WHERE created_at < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.Decisions => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM decisions
            WHERE ctid IN (
                SELECT ctid
                FROM decisions
                WHERE created_at < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.Actions => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM actions
            WHERE ctid IN (
                SELECT ctid
                FROM actions
                WHERE requested_at < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.AuditRecords => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM audit_records
            WHERE ctid IN (
                SELECT ctid
                FROM audit_records
                WHERE timestamp < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.DeadLetteredQueueMessages => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM queue_messages
            WHERE ctid IN (
                SELECT ctid
                FROM queue_messages
                WHERE dead_lettered = true
                  AND enqueued_at < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.ExpiredAdminSessions => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM admin_sessions
            WHERE ctid IN (
                SELECT ctid
                FROM admin_sessions
                WHERE (revoked_at IS NOT NULL AND revoked_at < {cutoff})
                   OR (revoked_at IS NULL AND absolute_expires_at < {cutoff})
                LIMIT {batchSize}
            );
            """, cancellationToken),

        RetentionTarget.LocalModelAdvisorConsults => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM local_model_advisor_consults
            WHERE ctid IN (
                SELECT ctid
                FROM local_model_advisor_consults
                WHERE created_at < {cutoff}
                LIMIT {batchSize}
            );
            """, cancellationToken),

        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };
}
