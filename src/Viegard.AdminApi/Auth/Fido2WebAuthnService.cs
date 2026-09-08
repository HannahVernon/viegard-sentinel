using System.Security.Cryptography;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using Viegard.Application.Auth;
using Viegard.Domain.Admin;

namespace Viegard.AdminApi.Auth;

public sealed class Fido2WebAuthnService(WebAuthnConfigurationProvider configurationProvider) : IWebAuthnService
{
    public ValueTask<WebAuthnBeginResult> BeginRegistrationAsync(
        AdminUser user,
        IReadOnlyCollection<byte[]> existingCredentialIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(existingCredentialIds);

        var fido2 = CreateFido2();
        var descriptors = existingCredentialIds.Select(id => new PublicKeyCredentialDescriptor(id.ToArray())).ToList();
        var selection = new AuthenticatorSelection
        {
            AuthenticatorAttachment = AuthenticatorAttachment.CrossPlatform,
            ResidentKey = ResidentKeyRequirement.Discouraged,
            // Explicitly required because fido2-net-lib v4 changed its default
            // unexpectedly; see upstream issue #658 and D-0032.
            UserVerification = UserVerificationRequirement.Required,
        };

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = ToFidoUser(user),
            ExcludeCredentials = descriptors,
            AuthenticatorSelection = selection,
            // Self-hosted operators enroll their own keys, so attestation
            // conveyance is not needed and would disclose device metadata.
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var json = options.ToJson();
        return ValueTask.FromResult(new WebAuthnBeginResult(json, json));
    }

    public async ValueTask<WebAuthnRegistrationResult> CompleteRegistrationAsync(
        string serverState,
        string clientResponseJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverState);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientResponseJson);

        try
        {
            var options = CredentialCreateOptions.FromJson(serverState);
            var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(clientResponseJson);
            if (response is null)
            {
                return new WebAuthnRegistrationResult(false, null, "Invalid WebAuthn attestation response.");
            }

            var credential = await CreateFido2().MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = static (_, _) => Task.FromResult(true),
            }, cancellationToken).ConfigureAwait(false);

            return new WebAuthnRegistrationResult(
                true,
                new WebAuthnNewCredential(
                    credential.Id.ToArray(),
                    credential.PublicKey.ToArray(),
                    credential.SignCount,
                    credential.AaGuid,
                    credential.Transports is null
                        ? null
                        : JsonSerializer.Serialize(credential.Transports, FidoModelSerializerContext.Default.AuthenticatorTransportArray)));
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or FormatException)
        {
            return new WebAuthnRegistrationResult(false, null, "Invalid WebAuthn attestation response.");
        }
    }

    public ValueTask<WebAuthnBeginResult> BeginAssertionAsync(
        IReadOnlyCollection<byte[]> allowedCredentialIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedCredentialIds);

        var descriptors = allowedCredentialIds.Select(id => new PublicKeyCredentialDescriptor(id.ToArray())).ToList();
        var options = CreateFido2().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = descriptors,
            // Explicitly required because fido2-net-lib issue #658 documents
            // an unsafe default change for Viegard's admin-hardware-key use.
            UserVerification = UserVerificationRequirement.Required,
        });

        var json = options.ToJson();
        return ValueTask.FromResult(new WebAuthnBeginResult(json, json));
    }

    public async ValueTask<WebAuthnAssertionResult> CompleteAssertionAsync(
        string serverState,
        string clientResponseJson,
        WebAuthnStoredCredential storedCredential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverState);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientResponseJson);
        ArgumentNullException.ThrowIfNull(storedCredential);

        if (storedCredential.SignCount < 0 || storedCredential.SignCount > uint.MaxValue)
        {
            return new WebAuthnAssertionResult(false, null, Error: "Invalid stored WebAuthn sign count.");
        }

        try
        {
            var options = AssertionOptions.FromJson(serverState);
            var response = JsonSerializer.Deserialize(
                clientResponseJson,
                FidoModelSerializerContext.Default.AuthenticatorAssertionRawResponse);
            if (response is null)
            {
                return new WebAuthnAssertionResult(false, null, Error: "Invalid WebAuthn assertion response.");
            }

            var result = await CreateFido2().MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = storedCredential.PublicKey.ToArray(),
                StoredSignatureCounter = (uint)storedCredential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(IsUserHandleOwnerOfCredential(args, storedCredential)),
            }, cancellationToken).ConfigureAwait(false);

            if (!WebAuthnSignCountPolicy.IsAcceptable(storedCredential.SignCount, result.SignCount))
            {
                return new WebAuthnAssertionResult(
                    false,
                    null,
                    CloneWarning: true,
                    Error: "WebAuthn authenticator counter regressed.");
            }

            return new WebAuthnAssertionResult(true, result.SignCount);
        }
        catch (Fido2VerificationException ex) when (ex.Code == Fido2ErrorCode.InvalidSignCount)
        {
            return new WebAuthnAssertionResult(
                false,
                null,
                CloneWarning: true,
                Error: "WebAuthn authenticator counter regressed.");
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or FormatException)
        {
            return new WebAuthnAssertionResult(false, null, Error: "Invalid WebAuthn assertion response.");
        }
    }

    private Fido2 CreateFido2()
    {
        var resolved = configurationProvider.Get();
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = resolved.RelyingPartyId,
            ServerName = "Viegard",
            Origins = resolved.Origins,
        }, null);
    }

    private static Fido2User ToFidoUser(AdminUser user) => new()
    {
        Id = user.Id.ToByteArray(),
        Name = user.Username,
        DisplayName = user.Username,
    };

    private static bool IsUserHandleOwnerOfCredential(
        IsUserHandleOwnerOfCredentialIdParams args,
        WebAuthnStoredCredential storedCredential)
    {
        if (args.CredentialId is null
            || args.UserHandle is null
            || args.CredentialId.Length != storedCredential.CredentialId.Length
            || !CryptographicOperations.FixedTimeEquals(args.CredentialId, storedCredential.CredentialId))
        {
            return false;
        }

        var expectedUserHandle = storedCredential.UserId.ToByteArray();
        return args.UserHandle.Length == 0
            || (args.UserHandle.Length == expectedUserHandle.Length
                && CryptographicOperations.FixedTimeEquals(args.UserHandle, expectedUserHandle));
    }
}
