namespace Viegard.Domain.Admin;

/// <summary>
/// An unhandled admin-request exception captured for operator review on the
/// step-up-gated /errors page.  The store keeps only the newest
/// <see cref="KeepNewest"/> rows, so the table is self-managing and needs no
/// retention configuration.  Messages and stack traces may contain sensitive
/// request data, which is why viewing requires step-up verification.
/// </summary>
public sealed record AdminError
{
    public const int MaxRequestIdLength = 128;
    public const int MaxPathLength = 512;
    public const int MaxMethodLength = 16;
    public const int MaxUsernameLength = 128;
    public const int MaxExceptionTypeLength = 256;
    public const int MaxMessageLength = 2000;
    public const int MaxStackTraceLength = 8000;
    public const int KeepNewest = 500;

    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string RequestId { get; init; }

    public required string Path { get; init; }

    public required string Method { get; init; }

    public string? Username { get; init; }

    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public required string StackTrace { get; init; }
}
