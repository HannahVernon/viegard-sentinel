namespace Viegard.Application.Policy;

public sealed record AutoActionCounts(int LastHour, int LastDay);

/// <summary>Persistence port for Judgment guardrail state.</summary>
public interface IGuardrailStateStore
{
    ValueTask RecordAutoActionAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken = default);

    ValueTask<AutoActionCounts> GetAutoActionCountsAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default);

    ValueTask RecordActionFailureAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken = default);

    ValueTask RecordActionSuccessAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken = default);

    ValueTask<int> GetConsecutiveActionFailuresAsync(CancellationToken cancellationToken = default);

    ValueTask SetCircuitBreakerOpenAsync(bool isOpen, CancellationToken cancellationToken = default);

    ValueTask<bool> IsCircuitBreakerOpenAsync(CancellationToken cancellationToken = default);

    ValueTask RecordIncidentAsync(
        string ip,
        Guid incidentId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    ValueTask<int> GetIncidentCountAsync(
        string ip,
        DateTimeOffset since,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);
}
