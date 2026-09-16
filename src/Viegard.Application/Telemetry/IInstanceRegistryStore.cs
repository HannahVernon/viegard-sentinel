using Viegard.Domain.Health;

namespace Viegard.Application.Telemetry;

/// <summary>Persistence port for the latest version reported by each running instance.</summary>
public interface IInstanceRegistryStore
{
    Task UpsertAsync(InstanceRegistration registration, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstanceRegistration>> ListAsync(CancellationToken cancellationToken = default);
}
