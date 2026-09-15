using Viegard.Domain.Admin;

namespace Viegard.Application.Auth;

/// <summary>
/// Library-free WebAuthn ceremony port.  Keeping fido2-net-lib types behind
/// this interface isolates the dependency to the AdminApi adapter as a
/// D-0032 supply-chain mitigation.
/// </summary>
public interface IWebAuthnService
{
    ValueTask<WebAuthnBeginResult> BeginRegistrationAsync(
        AdminUser user,
        IReadOnlyCollection<byte[]> existingCredentialIds,
        CancellationToken cancellationToken = default);

    ValueTask<WebAuthnRegistrationResult> CompleteRegistrationAsync(
        string serverState,
        string clientResponseJson,
        CancellationToken cancellationToken = default);

    ValueTask<WebAuthnBeginResult> BeginAssertionAsync(
        IReadOnlyCollection<byte[]> allowedCredentialIds,
        CancellationToken cancellationToken = default);

    ValueTask<WebAuthnAssertionResult> CompleteAssertionAsync(
        string serverState,
        string clientResponseJson,
        WebAuthnStoredCredential storedCredential,
        CancellationToken cancellationToken = default);
}

public sealed record WebAuthnBeginResult(string OptionsJson, string ServerState);

public sealed record WebAuthnRegistrationResult(
    bool Succeeded,
    WebAuthnNewCredential? Credential,
    string? Error = null);

public sealed record WebAuthnNewCredential(
    byte[] CredentialId,
    byte[] PublicKey,
    long SignCount,
    Guid Aaguid,
    string? Transports);

public sealed record WebAuthnStoredCredential(
    Guid UserId,
    byte[] CredentialId,
    byte[] PublicKey,
    long SignCount);

public sealed record WebAuthnAssertionResult(
    bool Succeeded,
    long? NewSignCount,
    bool CloneWarning = false,
    string? Error = null);
