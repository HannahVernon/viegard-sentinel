using Viegard.Domain.Admin;

namespace Viegard.Application.Stores;

/// <summary>Persistence port for local admin users and second-factor credentials.</summary>
public interface IAdminUserStore
{
    ValueTask<bool> AnyUsersAsync(CancellationToken cancellationToken = default);

    ValueTask<AdminUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<AdminUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    ValueTask CreateAsync(AdminUser user, CancellationToken cancellationToken = default);

    ValueTask UpdateAsync(AdminUser user, CancellationToken cancellationToken = default);

    ValueTask<AdminTotpSecret?> GetTotpSecretAsync(Guid userId, CancellationToken cancellationToken = default);

    ValueTask UpsertTotpSecretAsync(AdminTotpSecret secret, CancellationToken cancellationToken = default);

    ValueTask<bool> TrySetTotpLastAcceptedStepAsync(Guid userId, long acceptedStep, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminRecoveryCode>> GetRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default);

    ValueTask ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyList<AdminRecoveryCode> codes, CancellationToken cancellationToken = default);

    ValueTask<bool> TryMarkRecoveryCodeUsedAsync(Guid codeId, DateTimeOffset usedAt, CancellationToken cancellationToken = default);

    ValueTask<bool> AddWebAuthnCredentialAsync(
        AdminWebAuthnCredential credential,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminWebAuthnCredential>> ListWebAuthnCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    ValueTask<AdminWebAuthnCredential?> GetWebAuthnCredentialByCredentialIdAsync(
        byte[] credentialId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> UpdateWebAuthnCredentialUsageAsync(
        Guid id,
        long signCount,
        DateTimeOffset lastUsedAt,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteWebAuthnCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence port for revocable server-side admin sessions.</summary>
public interface IAdminSessionStore
{
    ValueTask CreateAsync(AdminSession session, CancellationToken cancellationToken = default);

    ValueTask<AdminSession?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminSession>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    ValueTask UpdateActivityAsync(
        Guid id,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        CancellationToken cancellationToken = default);

    ValueTask StampStepUpAsync(Guid id, DateTimeOffset stepUpAt, CancellationToken cancellationToken = default);

    ValueTask RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    ValueTask RevokeForUserAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        Guid? exceptSessionId = null,
        CancellationToken cancellationToken = default);
}
