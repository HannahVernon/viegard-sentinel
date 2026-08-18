using Viegard.Domain.Health;

namespace Viegard.Application.Telemetry;

/// <summary>Persistence port for queue telemetry snapshots (D-0012).</summary>
public interface IQueueTelemetryStore
{
    ValueTask PublishAsync(QueueTelemetrySnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Latest snapshot per (instance, queue).</summary>
    ValueTask<IReadOnlyList<QueueTelemetrySnapshot>> GetLatestAsync(CancellationToken cancellationToken = default);
}
