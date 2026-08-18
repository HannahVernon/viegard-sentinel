using Viegard.Domain.Events;

namespace Viegard.Application.Sources;

/// <summary>Result of attempting to normalize a raw observation.  Never throws for bad input.</summary>
public sealed record NormalizationResult
{
    public required bool Succeeded { get; init; }

    public NormalizedEvent? Event { get; init; }

    public string? FailureReason { get; init; }

    public static NormalizationResult Success(NormalizedEvent normalizedEvent) =>
        new() { Succeeded = true, Event = normalizedEvent };

    public static NormalizationResult Failure(string reason) =>
        new() { Succeeded = false, FailureReason = reason };
}

/// <summary>
/// Converts raw observations from one source family into the shared
/// normalized event model.  Malformed input yields a failure result (or a
/// <see cref="MalformedRecordPayload"/> event); it must never crash ingestion.
/// </summary>
public interface IEventNormalizer
{
    /// <summary>The source family this normalizer handles (e.g., "nginx").</summary>
    string SourceType { get; }

    NormalizationResult Normalize(RawObservation observation, string rawPayload);
}
