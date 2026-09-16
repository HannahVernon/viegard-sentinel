using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Viegard.Application.Configuration;
using Viegard.Application.Secrets;
using Viegard.Domain;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class MikroTikRouterValidationTests
{
    [Theory]
    [InlineData("border-1")]
    [InlineData("r1")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl")]
    public void Valid_names_are_accepted(string input)
    {
        var ok = MikroTikRouterValidator.TryNormalizeName(input, out var normalized, out var error);

        Assert.True(ok, error);
        Assert.Equal(input, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Border")]
    [InlineData("router_1")]
    [InlineData("router.example")]
    [InlineData("router 1")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklm")]
    public void Invalid_names_are_rejected(string input)
    {
        var ok = MikroTikRouterValidator.TryNormalizeName(input, out var normalized, out var error);

        Assert.False(ok);
        Assert.Null(normalized);
        Assert.Equal(MikroTikRouterValidator.NameValidationError, error);
    }

    [Theory]
    [InlineData("router")]
    [InlineData("ftp://router")]
    [InlineData("http://user:pass@router")]
    [InlineData("http://router/path")]
    [InlineData("http://router/?query=1")]
    [InlineData("http://router/#fragment")]
    public void Invalid_url_shapes_are_rejected(string input)
    {
        var ok = MikroTikRouterValidator.TryNormalizeBaseUrl(
            input,
            MikroTikRouterTransportMode.PlainHttp,
            out var normalized,
            out var error);

        Assert.False(ok);
        Assert.Null(normalized);
        Assert.Equal(MikroTikRouterValidator.BaseUrlShapeError, error);
    }

    [Fact]
    public void Url_normalization_keeps_only_authority()
    {
        var ok = MikroTikRouterValidator.TryNormalizeBaseUrl(
            "https://ROUTER.EXAMPLE.COM:8443/",
            MikroTikRouterTransportMode.HttpsTrustAny,
            out var normalized,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("https://router.example.com:8443", normalized);
    }

    [Fact]
    public void Plain_http_transport_requires_http_scheme()
    {
        var ok = MikroTikRouterValidator.TryNormalizeBaseUrl(
            "https://router.example.com",
            MikroTikRouterTransportMode.PlainHttp,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal(MikroTikRouterValidator.PlainHttpSchemeError, error);
    }

    [Theory]
    [InlineData(MikroTikRouterTransportMode.HttpsTrustAny)]
    [InlineData(MikroTikRouterTransportMode.HttpsPinned)]
    public void Https_transport_requires_https_scheme(MikroTikRouterTransportMode transportMode)
    {
        var ok = MikroTikRouterValidator.TryNormalizeBaseUrl(
            "http://router.example.com",
            transportMode,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal(MikroTikRouterValidator.HttpsSchemeError, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD")]
    public void Pinned_https_requires_lowercase_hex_sha256(string fingerprint)
    {
        var ok = MikroTikRouterValidator.TryNormalizePinnedCertificateSha256(
            fingerprint,
            MikroTikRouterTransportMode.HttpsPinned,
            out var normalized,
            out var error);

        Assert.False(ok);
        Assert.Null(normalized);
        Assert.Equal(MikroTikRouterValidator.PinnedCertificateRequiredError, error);
    }

    [Fact]
    public void Non_pinned_transport_rejects_fingerprint()
    {
        var ok = MikroTikRouterValidator.TryNormalizePinnedCertificateSha256(
            new string('a', 64),
            MikroTikRouterTransportMode.HttpsTrustAny,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal(MikroTikRouterValidator.PinnedCertificateBlankError, error);
    }

    [Fact]
    public void Username_rejects_control_characters()
    {
        var ok = MikroTikRouterValidator.TryNormalizeUsername("vie\ngard", out var normalized, out var error);

        Assert.False(ok);
        Assert.Null(normalized);
        Assert.Equal(MikroTikRouterValidator.UsernameValidationError, error);
    }
}

public sealed class AesGcmRouterCredentialProtectorTests
{
    [Fact]
    public void Round_trip_returns_original_password_and_randomizes_ciphertext()
    {
        var protector = Protector();
        var routerId = ViegardId.New();

        var first = protector.Protect(routerId, "router-secret");
        var second = protector.Protect(routerId, "router-secret");

        Assert.NotEqual(first, second);
        Assert.Equal("router-secret", protector.Unprotect(routerId, first));
        Assert.Equal("router-secret", protector.Unprotect(routerId, second));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var protector = Protector();
        var routerId = ViegardId.New();
        var payload = Convert.FromBase64String(protector.Protect(routerId, "router-secret"));
        payload[^1] ^= 0x01;

        var ex = Assert.Throws<RouterCredentialProtectionException>(() =>
            protector.Unprotect(routerId, Convert.ToBase64String(payload)));
        Assert.Contains("could not be authenticated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_router_id_aad_is_rejected()
    {
        var protector = Protector();
        var ciphertext = protector.Protect(ViegardId.New(), "router-secret");

        var ex = Assert.Throws<RouterCredentialProtectionException>(() =>
            protector.Unprotect(ViegardId.New(), ciphertext));
        Assert.Contains("could not be authenticated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_version_byte_is_rejected()
    {
        var protector = Protector();
        var routerId = ViegardId.New();
        var payload = Convert.FromBase64String(protector.Protect(routerId, "router-secret"));
        payload[0] = 0x02;

        var ex = Assert.Throws<RouterCredentialProtectionException>(() =>
            protector.Unprotect(routerId, Convert.ToBase64String(payload)));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQIDBA==")]
    public void Malformed_key_secret_fails_closed(string keyValue)
    {
        var protector = Protector(keyValue);

        var ex = Assert.Throws<RouterCredentialProtectionException>(() =>
            protector.Protect(ViegardId.New(), "router-secret"));
        Assert.Contains(AesGcmRouterCredentialProtector.SecretName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("exactly 32 random bytes", ex.Message, StringComparison.Ordinal);
    }

    private static IRouterCredentialProtector Protector(string? keyValue = null)
    {
        var key = keyValue ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AesGcmRouterCredentialProtector.SecretName] = key,
                [$"Secrets:{AesGcmRouterCredentialProtector.SecretName}"] = key,
                [$"Viegard:Secrets:{AesGcmRouterCredentialProtector.SecretName}"] = key,
                [$"Viegard:Secrets:Values:{AesGcmRouterCredentialProtector.SecretName}"] = key,
            })
            .Build();
        return new AesGcmRouterCredentialProtector(new ConfigurationSecretProvider(configuration));
    }
}

public sealed class InMemoryMikroTikRouterStoreTests
{
    [Fact]
    public async Task Crud_enforces_uniqueness_concurrency_and_credential_updates()
    {
        var store = new InMemoryMikroTikRouterStore();
        var first = Router("router-a");
        var second = Router("router-b");

        var created = await store.CreateAsync(first, "cipher-1");
        Assert.True(created.Succeeded);
        Assert.Equal(1, created.Router!.RowVersion);
        Assert.Equal("cipher-1", await store.GetCredentialCiphertextAsync(first.Id));

        var duplicate = await store.CreateAsync(second with { Name = "router-a" }, "cipher-dup");
        Assert.Equal(MikroTikRouterSaveStatus.DuplicateName, duplicate.Status);

        var updated = await store.UpdateAsync(
            created.Router with
            {
                BaseUrl = "http://router-a.example.com:8080",
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
                UpdatedBy = "operator",
            },
            expectedRowVersion: created.Router.RowVersion);
        Assert.True(updated.Succeeded);
        Assert.Equal(2, updated.Router!.RowVersion);
        Assert.Equal("cipher-1", await store.GetCredentialCiphertextAsync(first.Id));

        var rotated = await store.UpdateAsync(
            updated.Router with
            {
                Username = "viegard2",
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(2),
                UpdatedBy = "operator",
            },
            expectedRowVersion: updated.Router.RowVersion,
            passwordCiphertext: "cipher-2");
        Assert.True(rotated.Succeeded);
        Assert.Equal("cipher-2", await store.GetCredentialCiphertextAsync(first.Id));

        var conflict = await store.UpdateAsync(
            updated.Router with { Username = "stale", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(3) },
            expectedRowVersion: updated.Router.RowVersion);
        Assert.Equal(MikroTikRouterSaveStatus.Conflict, conflict.Status);
        Assert.Equal(3, conflict.Router!.RowVersion);

        var secondCreated = await store.CreateAsync(second, "cipher-3");
        Assert.True(secondCreated.Succeeded);
        var nameConflict = await store.UpdateAsync(
            secondCreated.Router! with { Name = "router-a", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(4) },
            expectedRowVersion: secondCreated.Router.RowVersion);
        Assert.Equal(MikroTikRouterSaveStatus.DuplicateName, nameConflict.Status);

        var deleteConflict = await store.DeleteAsync(first.Id, expectedRowVersion: 1);
        Assert.Equal(MikroTikRouterDeleteStatus.Conflict, deleteConflict.Status);

        var deleted = await store.DeleteAsync(first.Id, rotated.Router!.RowVersion);
        Assert.True(deleted.Succeeded);
        Assert.Null(await store.GetAsync(first.Id));
        Assert.Null(await store.GetCredentialCiphertextAsync(first.Id));
    }

    private static MikroTikRouter Router(string name) => new()
    {
        Id = ViegardId.New(),
        Name = name,
        BaseUrl = $"http://{name}.example.com",
        TransportMode = MikroTikRouterTransportMode.PlainHttp,
        PinnedCertificateSha256 = null,
        Username = "viegard",
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
        RowVersion = 0,
    };
}
