using Viegard.Domain.Classifications;

namespace Viegard.Application.Classifiers;

/// <summary>Why a classification attempt failed.</summary>
public enum ClassificationFailureKind
{
    ProviderUnavailable,
    Timeout,
    MalformedOutput,
    SchemaValidationFailed,
    Overloaded,
    Cancelled,
    Unknown,
}

/// <summary>
/// Result of a classification attempt.  Failure is a first-class outcome:
/// the pipeline retains the subject, records the failure, and never becomes
/// more permissive because classification failed.
/// </summary>
public sealed record ClassificationOutcome
{
    public required bool Succeeded { get; init; }

    public Domain.Classifications.Classification? Classification { get; init; }

    public ClassificationFailureKind? FailureKind { get; init; }

    public string? FailureDetail { get; init; }

    public static ClassificationOutcome Success(Domain.Classifications.Classification classification) =>
        new() { Succeeded = true, Classification = classification };

    public static ClassificationOutcome Failure(ClassificationFailureKind kind, string? detail = null) =>
        new() { Succeeded = false, FailureKind = kind, FailureDetail = detail };
}

/// <summary>Subject handed to a classifier: an incident or a mail message, by ID.</summary>
public sealed record ClassificationSubject
{
    public required ClassificationSubjectKind Kind { get; init; }

    public required Guid SubjectId { get; init; }
}

/// <summary>
/// A classifier (Mind): deterministic rules, heuristics, allow/denylists, or
/// AI-backed implementations.  Classifiers recommend; they never act.
/// </summary>
public interface IClassifier
{
    string ClassifierId { get; }

    Task<ClassificationOutcome> ClassifyAsync(ClassificationSubject subject, CancellationToken cancellationToken = default);
}
