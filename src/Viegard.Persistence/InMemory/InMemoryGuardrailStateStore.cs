using System.Collections.Concurrent;
using System.Net;
using Viegard.Application.Policy;

namespace Viegard.Persistence.InMemory;

/// <summary>Development-only, non-durable guardrail state store.</summary>
public sealed class InMemoryGuardrailStateStore : IGuardrailStateStore
{
    private readonly Lock _lock = new();
    private readonly List<DateTimeOffset> _autoActions = [];
    private readonly Dictionary<string, Dictionary<Guid, DateTimeOffset>> _incidentsByIp =
        new(StringComparer.OrdinalIgnoreCase);
    private int _consecutiveActionFailures;
    private bool _circuitBreakerOpen;

    public ValueTask RecordAutoActionAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _autoActions.Add(occurredAt);
            PruneAutoActions(occurredAt.Subtract(TimeSpan.FromDays(1)));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<AutoActionCounts> GetAutoActionCountsAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            PruneAutoActions(asOf.Subtract(TimeSpan.FromDays(1)));
            var hourStart = asOf.Subtract(TimeSpan.FromHours(1));
            var dayStart = asOf.Subtract(TimeSpan.FromDays(1));
            return ValueTask.FromResult(new AutoActionCounts(
                _autoActions.Count(t => t >= hourStart && t <= asOf),
                _autoActions.Count(t => t >= dayStart && t <= asOf)));
        }
    }

    public ValueTask RecordActionFailureAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _consecutiveActionFailures++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordActionSuccessAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _consecutiveActionFailures = 0;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetConsecutiveActionFailuresAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_consecutiveActionFailures);
        }
    }

    public ValueTask SetCircuitBreakerOpenAsync(bool isOpen, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _circuitBreakerOpen = isOpen;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsCircuitBreakerOpenAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_circuitBreakerOpen);
        }
    }

    public ValueTask RecordIncidentAsync(
        string ip,
        Guid incidentId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeIpKey(ip);
        lock (_lock)
        {
            if (!_incidentsByIp.TryGetValue(key, out var incidents))
            {
                incidents = [];
                _incidentsByIp[key] = incidents;
            }

            incidents[incidentId] = occurredAt;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetIncidentCountAsync(
        string ip,
        DateTimeOffset since,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeIpKey(ip);
        lock (_lock)
        {
            if (!_incidentsByIp.TryGetValue(key, out var incidents))
            {
                return ValueTask.FromResult(0);
            }

            return ValueTask.FromResult(incidents.Values.Count(t => t >= since && t <= asOf));
        }
    }

    private void PruneAutoActions(DateTimeOffset oldestToKeep) =>
        _autoActions.RemoveAll(t => t < oldestToKeep);

    private static string NormalizeIpKey(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            throw new ArgumentException("IP value is required.", nameof(ip));
        }

        if (!IPAddress.TryParse(ip.Trim(), out var address))
        {
            return ip.Trim().ToLowerInvariant();
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
