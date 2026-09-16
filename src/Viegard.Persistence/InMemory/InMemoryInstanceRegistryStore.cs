using System.Collections.Concurrent;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryInstanceRegistryStore : IInstanceRegistryStore
{
    private readonly ConcurrentDictionary<string, InstanceRegistration> _registrations = new(StringComparer.Ordinal);

    public Task UpsertAsync(InstanceRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _registrations[registration.InstanceId] = registration;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InstanceRegistration>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstanceRegistration>>(
            _registrations.Values.OrderBy(r => r.InstanceId, StringComparer.Ordinal).ToList());

    public Task<int> DeleteStaleAsync(DateTimeOffset reportedBefore, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var pair in _registrations)
        {
            if (pair.Value.ReportedAt < reportedBefore && _registrations.TryRemove(pair))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }
}
