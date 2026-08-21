using System.Collections.Concurrent;
using Viegard.Application.Telemetry;
using Viegard.Domain.Health;

namespace Viegard.Persistence.InMemory;

/// <summary>
/// Development-only telemetry store.  Cross-process queue monitoring (admin
/// reading pipeline telemetry) requires the durable store that arrives with
/// the database decision (D-0004); until then this works within one process.
/// </summary>
public sealed class InMemoryQueueTelemetryStore : IQueueTelemetryStore
{
    private readonly ConcurrentDictionary<(string InstanceId, string QueueName), QueueTelemetrySnapshot> _latest = new();

    public ValueTask PublishAsync(QueueTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latest[(snapshot.InstanceId, snapshot.QueueName)] = snapshot;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<QueueTelemetrySnapshot>> GetLatestAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<QueueTelemetrySnapshot>>(
            _latest.Values.OrderBy(s => s.InstanceId).ThenBy(s => s.QueueName).ToList());
}
