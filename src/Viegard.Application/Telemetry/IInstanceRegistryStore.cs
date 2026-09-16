using Viegard.Domain.Health;

namespace Viegard.Application.Telemetry;

/// <summary>Persistence port for the latest version reported by each running instance.</summary>
public interface IInstanceRegistryStore
{
    Task UpsertAsync(InstanceRegistration registration, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstanceRegistration>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes rows whose <see cref="InstanceRegistration.ReportedAt"/> is
    /// older than the cutoff.  Live instances re-report on every heartbeat,
    /// so only rows from stopped or renamed instances qualify.
    /// </summary>
    Task<int> DeleteStaleAsync(DateTimeOffset reportedBefore, CancellationToken cancellationToken = default);
}
