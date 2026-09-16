namespace Viegard.Actions.MikroTik;

public sealed record MikroTikRouterActionResult
{
    public required Guid RouterId { get; init; }

    public required string RouterName { get; init; }

    public required int Attempts { get; init; }

    public required string Status { get; init; }

    public required string Detail { get; init; }

    public required DateTimeOffset LastAttemptAt { get; init; }
}
