using Viegard.Domain.Health;

namespace Viegard.Application.Health;

/// <summary>
/// A component that reports its own health (IMAP connection, log ingestion,
/// inference backend, queue, action provider).  Hosts aggregate contributors
/// into liveness/readiness endpoints and persisted telemetry.
/// </summary>
public interface IHealthContributor
{
    string ComponentId { get; }

    Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken = default);
}
