namespace Viegard.Domain.Health;

public sealed record InstanceRegistration
{
    public required string InstanceId { get; init; }

    public required string Version { get; init; }

    public string? CommitSha { get; init; }

    public required string Roles { get; init; }

    public required string HostName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset ReportedAt { get; init; }
}
