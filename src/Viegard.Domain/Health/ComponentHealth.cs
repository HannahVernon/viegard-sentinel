namespace Viegard.Domain.Health;

public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>Point-in-time health of one component.</summary>
public sealed record ComponentHealth
{
    public required string ComponentId { get; init; }

    public required HealthStatus Status { get; init; }

    public string? Detail { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }
}
