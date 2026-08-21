using Npgsql;
using Viegard.Application.Queues;
using Viegard.Domain.Commands;

namespace Viegard.Persistence.Postgres.Queues;

/// <summary>
/// Durable admin command queue (the only write path from the admin service
/// to pipeline hosts) implemented on the shared PostgreSQL queue mechanics.
/// </summary>
public sealed class PostgresCommandQueue(NpgsqlDataSource dataSource) : ICommandQueue
{
    public const string CommandQueueName = "admin-commands";

    private readonly PostgresWorkQueue<AdminCommand> _inner = new(dataSource, CommandQueueName);

    public ValueTask EnqueueAsync(AdminCommand command, CancellationToken cancellationToken = default) =>
        _inner.EnqueueAsync(command, cancellationToken);

    public ValueTask<IWorkLease<AdminCommand>> LeaseAsync(CancellationToken cancellationToken = default) =>
        _inner.LeaseAsync(cancellationToken);

    public ValueTask<WorkQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetStatsAsync(cancellationToken);
}
