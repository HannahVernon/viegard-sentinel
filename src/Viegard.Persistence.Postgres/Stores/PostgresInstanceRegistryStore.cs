using Microsoft.EntityFrameworkCore;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresInstanceRegistryStore(IDbContextFactory<ViegardDbContext> factory) : IInstanceRegistryStore
{
    public async Task UpsertAsync(
        InstanceRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var row = registration.ToRow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO instance_registry (
                instance_id,
                version,
                commit_sha,
                roles,
                host_name,
                started_at,
                reported_at
            )
            VALUES (
                {row.InstanceId},
                {row.Version},
                {row.CommitSha},
                {row.Roles},
                {row.HostName},
                {row.StartedAt},
                {row.ReportedAt}
            )
            ON CONFLICT (instance_id) DO UPDATE SET
                version = EXCLUDED.version,
                commit_sha = EXCLUDED.commit_sha,
                roles = EXCLUDED.roles,
                host_name = EXCLUDED.host_name,
                started_at = EXCLUDED.started_at,
                reported_at = EXCLUDED.reported_at;
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InstanceRegistration>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.InstanceRegistry.AsNoTracking()
            .OrderBy(r => r.InstanceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }
}
