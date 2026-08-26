namespace Viegard.Domain.Admin;

/// <summary>Local operator account for the admin interface.</summary>
public sealed record AdminUser
{
    public required Guid Id { get; init; }

    public required string Username { get; init; }

    public required string PasswordHash { get; init; }

    public required DateTimeOffset PasswordChangedAt { get; init; }

    public required int FailedLoginCount { get; init; }

    public DateTimeOffset? LockedUntil { get; init; }

    public required bool MustChangePassword { get; init; }

    public required bool TotpEnrolled { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>First-party RFC 6238 TOTP credential for an admin user.</summary>
public sealed record AdminTotpSecret
{
    public required Guid UserId { get; init; }

    public required string SecretBase32 { get; init; }

    public long? LastAcceptedStep { get; init; }

    public required DateTimeOffset EnrolledAt { get; init; }
}

/// <summary>Single-use recovery code hash for an admin user.</summary>
public sealed record AdminRecoveryCode
{
    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required string CodeHash { get; init; }

    public DateTimeOffset? UsedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Server-side session backing the encrypted admin cookie.</summary>
public sealed record AdminSession
{
    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public required DateTimeOffset AbsoluteExpiresAt { get; init; }

    public required DateTimeOffset IdleExpiresAt { get; init; }

    public required string Ip { get; init; }

    public required string IpBindingMode { get; init; }

    public required string UserAgent { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public DateTimeOffset? StepUpAt { get; init; }
}
