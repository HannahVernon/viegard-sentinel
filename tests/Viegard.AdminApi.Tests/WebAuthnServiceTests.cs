using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.AdminApi.Auth;
using Viegard.AdminApi.Configuration;
using Viegard.Application.Auth;
using Viegard.Domain;
using Viegard.Domain.Admin;

namespace Viegard.AdminApi.Tests;

public sealed class WebAuthnServiceTests
{
    [Fact]
    public async Task Registration_options_are_random_and_require_user_verification()
    {
        var service = CreateService(
            new AdminWebAuthnOptions
            {
                RelyingPartyId = "viegard.example.com",
                Origins = ["https://viegard.example.com"],
            },
            AdminExposureModes.Direct);
        var user = CreateUser();

        var first = await service.BeginRegistrationAsync(user, [[1, 2, 3]]);
        var second = await service.BeginRegistrationAsync(user, [[1, 2, 3]]);

        using var firstJson = JsonDocument.Parse(first.OptionsJson);
        using var secondJson = JsonDocument.Parse(second.OptionsJson);
        Assert.Equal("viegard.example.com", firstJson.RootElement.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("required", firstJson.RootElement.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        Assert.Equal("cross-platform", firstJson.RootElement.GetProperty("authenticatorSelection").GetProperty("authenticatorAttachment").GetString());
        Assert.Equal("none", firstJson.RootElement.GetProperty("attestation").GetString());
        Assert.NotEqual(
            firstJson.RootElement.GetProperty("challenge").GetString(),
            secondJson.RootElement.GetProperty("challenge").GetString());
        Assert.True(WebAuthnBase64Url.TryDecode(firstJson.RootElement.GetProperty("challenge").GetString(), out var challenge));
        Assert.NotEmpty(challenge);
    }

    [Fact]
    public async Task Assertion_options_honor_rp_id_and_require_user_verification()
    {
        var service = CreateService(
            new AdminWebAuthnOptions
            {
                RelyingPartyId = "viegard.example.com",
                Origins = ["https://viegard.example.com"],
            },
            AdminExposureModes.Direct);

        var result = await service.BeginAssertionAsync([[10, 11, 12]]);

        using var json = JsonDocument.Parse(result.OptionsJson);
        Assert.Equal("viegard.example.com", json.RootElement.GetProperty("rpId").GetString());
        Assert.Equal("required", json.RootElement.GetProperty("userVerification").GetString());
        var id = json.RootElement.GetProperty("allowCredentials")[0].GetProperty("id").GetString();
        Assert.True(WebAuthnBase64Url.TryDecode(id, out var decoded));
        Assert.Equal([10, 11, 12], decoded);
    }

    [Fact]
    public async Task Loopback_exposure_defaults_to_localhost()
    {
        var service = CreateService(new AdminWebAuthnOptions(), AdminExposureModes.Loopback);
        var result = await service.BeginRegistrationAsync(CreateUser(), []);

        using var json = JsonDocument.Parse(result.OptionsJson);
        Assert.Equal("localhost", json.RootElement.GetProperty("rp").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Non_loopback_exposure_requires_explicit_rp_id_and_origin()
    {
        var service = CreateService(new AdminWebAuthnOptions(), AdminExposureModes.Direct);

        await Assert.ThrowsAsync<WebAuthnConfigurationException>(async () =>
            await service.BeginRegistrationAsync(CreateUser(), []));
    }

    private static Fido2WebAuthnService CreateService(AdminWebAuthnOptions webAuthnOptions, string exposure) =>
        new(new WebAuthnConfigurationProvider(
            Options.Create(webAuthnOptions),
            Options.Create(new AdminExposureOptions { Exposure = exposure })));

    private static AdminUser CreateUser()
    {
        var now = DateTimeOffset.UtcNow;
        return new AdminUser
        {
            Id = ViegardId.New(),
            Username = "admin",
            PasswordHash = "hash",
            PasswordChangedAt = now,
            FailedLoginCount = 0,
            LockedUntil = null,
            MustChangePassword = false,
            TotpEnrolled = true,
            CreatedAt = now,
        };
    }
}
