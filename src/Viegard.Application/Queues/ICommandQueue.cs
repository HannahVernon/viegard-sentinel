using Viegard.Domain.Commands;

namespace Viegard.Application.Queues;

/// <summary>
/// Durable queue of admin commands: the only write path from the admin
/// service to the pipeline host.  Implementations must be durable (survive
/// restarts) because approvals and unblocks must not be lost.
/// </summary>
public interface ICommandQueue
{
    ValueTask EnqueueAsync(AdminCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lease the next pending command for validation and execution.</summary>
    ValueTask<IWorkLease<AdminCommand>> LeaseAsync(CancellationToken cancellationToken = default);

    WorkQueueStats GetStats();
}
