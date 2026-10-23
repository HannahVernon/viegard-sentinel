using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Viegard.Application.Auth;
using Viegard.Application.Configuration;
using Viegard.Application.Secrets;

namespace Viegard.Application.Tests;

public sealed class TotpAtRestProtectionTests
{
    private const string Secret = "JBSWY3DPEHPK3PXP";

    [Fact]
    public void Protect_then_unprotect_round_trips_and_hides_the_secret()
    {
        var protector = Create();
        var user = Guid.NewGuid();

        var stored = protector.Protect(user, Secret);

        Assert.True(protector.IsProtected(stored));
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.Equal(Secret, protector.Unprotect(user, stored));
    }

    [Fact]
    public void Protecting_twice_uses_a_fresh_nonce()
    {
        var protector = Create();
        var user = Guid.NewGuid();

        Assert.NotEqual(protector.Protect(user, Secret), protector.Protect(user, Secret));
    }

    [Fact]
    public void Ciphertext_copied_to_another_user_fails_authentication()
    {
        var protector = Create();
        var stored = protector.Protect(Guid.NewGuid(), Secret);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), stored));
    }

    [Fact]
    public void Legacy_plaintext_passes_through()
    {
        var protector = Create();

        Assert.False(protector.IsProtected(Secret));
        Assert.Equal(Secret, protector.Unprotect(Guid.NewGuid(), Secret));
    }

    [Fact]
    public void Tampered_or_truncated_ciphertext_throws()
    {
        var protector = Create();
        var user = Guid.NewGuid();
        var stored = protector.Protect(user, Secret);
        var bytes = Convert.FromBase64String(stored["enc1:".Length..]);
        bytes[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect(user, "enc1:" + Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect(user, "enc1:" + Convert.ToBase64String(new byte[10])));
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(user, "enc1:not base64!"));
    }

    [Fact]
    public void A_different_master_key_fails_authentication()
    {
        var user = Guid.NewGuid();
        var stored = Create().Protect(user, Secret);

        Assert.ThrowsAny<CryptographicException>(() => Create(RandomNumberGenerator.GetBytes(32)).Unprotect(user, stored));
    }

    private static AesGcmSecondFactorSecretProtector Create(byte[]? key = null)
    {
        var value = Convert.ToBase64String(key ?? new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AesGcmRouterCredentialProtector.SecretName] = value,
                [$"Secrets:{AesGcmRouterCredentialProtector.SecretName}"] = value,
                [$"Viegard:Secrets:{AesGcmRouterCredentialProtector.SecretName}"] = value,
                [$"Viegard:Secrets:Values:{AesGcmRouterCredentialProtector.SecretName}"] = value,
            })
            .Build();
        return new AesGcmSecondFactorSecretProtector(new ConfigurationSecretProvider(configuration));
    }
}
