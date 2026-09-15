namespace Viegard.Domain.Admin;

/// <summary>WebAuthn public-key credential enrolled for an admin operator.</summary>
public sealed record AdminWebAuthnCredential
{
    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required byte[] CredentialId { get; init; }

    public required byte[] PublicKey { get; init; }

    public required long SignCount { get; init; }

    public required Guid Aaguid { get; init; }

    public string? Transports { get; init; }

    /// <summary>Operator-given label.  Treat as untrusted.</summary>
    public required string Name { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }
}
