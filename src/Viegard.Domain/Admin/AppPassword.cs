namespace Viegard.Domain.Admin;

/// <summary>
/// A revocable, read-only API token ("app password") owned by an admin user.
/// The token secret is never stored: only its SHA-256 hash is persisted, and
/// the plaintext is shown exactly once at creation.  App-password principals
/// can never satisfy step-up and are only accepted by endpoints explicitly
/// opted into the read-only API policy.
/// </summary>
public sealed record AppPassword
{
    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>Operator-chosen label, for example "copilot-cli".</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The token's embedded lookup segment (16 lowercase hex characters),
    /// unique per token so validation is a point lookup rather than a scan.
    /// </summary>
    public required string LookupKey { get; init; }

    /// <summary>Lowercase hex SHA-256 of the full token string.</summary>
    public required string SecretHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
