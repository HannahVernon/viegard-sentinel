using System.Net;
using Viegard.Application.Auth;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class AdminAuthTests
{
    [Theory]
    [InlineData("foo", "MZXW6")]
    [InlineData("hello world", "NBSWY3DPEB3W64TMMQ")]
    public void Base32_round_trips(string value, string expected)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        var encoded = Base32Encoding.Encode(bytes);

        Assert.Equal(expected, encoded);
        Assert.Equal(bytes, Base32Encoding.Decode(encoded));
    }

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void Totp_matches_rfc_6238_sha1_vectors(long unixTime, string expected)
    {
        var secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        var code = TotpService.ComputeCode(secret, DateTimeOffset.FromUnixTimeSeconds(unixTime), digits: 8);

        Assert.Equal(expected, code);
    }

    [Fact]
    public void Totp_accepts_adjacent_step_and_rejects_replay()
    {
        var secret = Base32Encoding.Encode(System.Text.Encoding.ASCII.GetBytes("12345678901234567890"));
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(90);
        var previousStepCode = TotpService.ComputeCode(
            System.Text.Encoding.ASCII.GetBytes("12345678901234567890"),
            TotpService.ToStep(timestamp) - 1);

        Assert.True(TotpService.VerifyCode(secret, previousStepCode, null, timestamp, 6, out var acceptedStep));
        Assert.False(TotpService.VerifyCode(secret, previousStepCode, acceptedStep, timestamp, 6, out _));
    }

    [Fact]
    public async Task Recovery_codes_are_single_use_and_regeneration_invalidates_previous_codes()
    {
        var service = new RecoveryCodeService();
        var store = new InMemoryAdminUserStore();
        var userId = ViegardId.New();
        var firstBatch = service.GenerateCodes();
        await store.ReplaceRecoveryCodesAsync(userId, service.ToRows(userId, firstBatch, DateTimeOffset.UtcNow));

        var stored = await store.GetRecoveryCodesAsync(userId);
        var match = stored.Single(c => RecoveryCodeService.Verify(firstBatch[0], c.CodeHash));

        Assert.True(await store.TryMarkRecoveryCodeUsedAsync(match.Id, DateTimeOffset.UtcNow));
        Assert.False(await store.TryMarkRecoveryCodeUsedAsync(match.Id, DateTimeOffset.UtcNow));

        var secondBatch = service.GenerateCodes();
        await store.ReplaceRecoveryCodesAsync(userId, service.ToRows(userId, secondBatch, DateTimeOffset.UtcNow));

        var regenerated = await store.GetRecoveryCodesAsync(userId);
        Assert.DoesNotContain(regenerated, c => RecoveryCodeService.Verify(firstBatch[1], c.CodeHash));
        Assert.Contains(regenerated, c => RecoveryCodeService.Verify(secondBatch[0], c.CodeHash));
    }

    [Fact]
    public async Task In_memory_webauthn_credentials_round_trip_and_enforce_unique_ids()
    {
        var store = new InMemoryAdminUserStore();
        var now = DateTimeOffset.UtcNow;
        var userId = ViegardId.New();
        var credential = new AdminWebAuthnCredential
        {
            Id = ViegardId.New(),
            UserId = userId,
            CredentialId = [1, 2, 3, 4],
            PublicKey = [5, 6, 7, 8],
            SignCount = 12,
            Aaguid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Transports = "[\"usb\"]",
            Name = "primary key",
            CreatedAt = now,
            LastUsedAt = null,
        };

        Assert.True(await store.AddWebAuthnCredentialAsync(credential));
        Assert.False(await store.AddWebAuthnCredentialAsync(credential with { Id = ViegardId.New(), UserId = ViegardId.New() }));

        var listed = Assert.Single(await store.ListWebAuthnCredentialsAsync(userId));
        Assert.Equal(credential.CredentialId, listed.CredentialId);
        Assert.Equal(credential.PublicKey, listed.PublicKey);
        Assert.Equal("primary key", listed.Name);

        var found = await store.GetWebAuthnCredentialByCredentialIdAsync([1, 2, 3, 4]);
        Assert.NotNull(found);
        Assert.Equal(credential.Id, found.Id);

        var usedAt = now.AddMinutes(5);
        Assert.True(await store.UpdateWebAuthnCredentialUsageAsync(credential.Id, 13, usedAt));
        found = await store.GetWebAuthnCredentialByCredentialIdAsync([1, 2, 3, 4]);
        Assert.Equal(13, found!.SignCount);
        Assert.Equal(usedAt.ToUniversalTime(), found.LastUsedAt);

        Assert.False(await store.DeleteWebAuthnCredentialAsync(ViegardId.New(), credential.Id));
        Assert.True(await store.DeleteWebAuthnCredentialAsync(userId, credential.Id));
        Assert.Empty(await store.ListWebAuthnCredentialsAsync(userId));
    }

    [Fact]
    public async Task In_memory_sessions_support_expiry_revocation_and_activity_refresh()
    {
        var store = new InMemoryAdminSessionStore();
        var now = DateTimeOffset.UtcNow;
        var userId = ViegardId.New();
        var session = new AdminSession
        {
            Id = ViegardId.New(),
            UserId = userId,
            CreatedAt = now.AddMinutes(-10),
            LastSeenAt = now.AddMinutes(-2),
            AbsoluteExpiresAt = now.AddDays(1),
            IdleExpiresAt = now.AddHours(1),
            Ip = "192.0.2.10",
            IpBindingMode = AdminIpBindingModes.Strict,
            UserAgent = "test",
        };

        await store.CreateAsync(session);
        var valid = AdminSessionValidator.Validate(
            await store.GetAsync(session.Id),
            userId,
            IPAddress.Parse("192.0.2.10"),
            now,
            TimeSpan.FromHours(1));

        Assert.True(valid.IsValid);
        Assert.True(valid.ShouldRefreshActivity);

        await store.UpdateActivityAsync(session.Id, now, now.AddHours(1));
        var refreshed = await store.GetAsync(session.Id);
        Assert.Equal(now, refreshed!.LastSeenAt);

        var notYetThrottled = AdminSessionValidator.Validate(
            refreshed,
            userId,
            IPAddress.Parse("192.0.2.10"),
            now.AddSeconds(30),
            TimeSpan.FromHours(1));
        Assert.True(notYetThrottled.IsValid);
        Assert.False(notYetThrottled.ShouldRefreshActivity);

        var idleExpired = AdminSessionValidator.Validate(
            refreshed with { IdleExpiresAt = now.AddSeconds(-1) },
            userId,
            IPAddress.Parse("192.0.2.10"),
            now,
            TimeSpan.FromHours(1));
        Assert.False(idleExpired.IsValid);

        var absoluteExpired = AdminSessionValidator.Validate(
            refreshed with { AbsoluteExpiresAt = now.AddSeconds(-1) },
            userId,
            IPAddress.Parse("192.0.2.10"),
            now,
            TimeSpan.FromHours(1));
        Assert.False(absoluteExpired.IsValid);

        await store.RevokeAsync(session.Id, now);
        var revoked = AdminSessionValidator.Validate(
            await store.GetAsync(session.Id),
            userId,
            IPAddress.Parse("192.0.2.10"),
            now,
            TimeSpan.FromHours(1));
        Assert.False(revoked.IsValid);
    }

    [Theory]
    [InlineData("strict", "198.51.100.10", "198.51.100.10", true, true)]
    [InlineData("strict", "198.51.100.10", "198.51.100.11", false, false)]
    [InlineData("subnet", "198.51.100.10", "198.51.100.200", true, true)]
    [InlineData("subnet", "198.51.100.10", "198.51.101.10", false, false)]
    [InlineData("subnet", "2001:db8:abcd:1::10", "2001:db8:abcd:1::99", true, true)]
    [InlineData("subnet", "2001:db8:abcd:1::10", "2001:db8:abcd:2::10", false, false)]
    [InlineData("log-only", "198.51.100.10", "198.51.100.11", true, false)]
    public void Ip_binding_modes_have_expected_semantics(
        string mode,
        string original,
        string current,
        bool expectedAllowed,
        bool expectedMatched)
    {
        var result = AdminIpBinding.Evaluate(mode, original, IPAddress.Parse(current));

        Assert.Equal(expectedAllowed, result.Allowed);
        Assert.Equal(expectedMatched, result.Matched);
    }

    [Fact]
    public void Password_policy_rejects_short_passwords()
    {
        Assert.False(PasswordPolicy.IsValid("short", out var error));
        Assert.Contains("20", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_auth_options_validator_enforces_absolute_lifetime_cap()
    {
        var validator = new AdminAuthOptionsValidator();

        var result = validator.Validate(null, new AdminAuthOptions { AbsoluteLifetime = TimeSpan.FromDays(31) });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Allowed_sources_allow_loopback_and_configured_cidrs_only()
    {
        var loopbackOnly = new AdminAllowedSourceGate([]);
        Assert.True(loopbackOnly.IsAllowed(IPAddress.Loopback));
        Assert.True(loopbackOnly.IsAllowed(IPAddress.IPv6Loopback));
        Assert.False(loopbackOnly.IsAllowed(IPAddress.Parse("198.51.100.10")));

        var configured = new AdminAllowedSourceGate(["198.51.100.0/24", "2001:db8:abcd::/48"]);
        Assert.True(configured.IsAllowed(IPAddress.Parse("198.51.100.10")));
        Assert.True(configured.IsAllowed(IPAddress.Parse("2001:db8:abcd::10")));
        Assert.False(configured.IsAllowed(IPAddress.Parse("203.0.113.10")));
    }

    [Fact]
    public void Webauthn_base64url_round_trips_without_padding()
    {
        var bytes = new byte[] { 0, 1, 2, 250, 251, 252, 253, 254, 255 };
        var encoded = WebAuthnBase64Url.Encode(bytes);

        Assert.DoesNotContain("+", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("/", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("=", encoded, StringComparison.Ordinal);
        Assert.True(WebAuthnBase64Url.TryDecode(encoded, out var decoded));
        Assert.Equal(bytes, decoded);
        Assert.False(WebAuthnBase64Url.TryDecode("abcde", out _));
    }

    [Fact]
    public void Webauthn_sign_count_policy_rejects_regressions()
    {
        Assert.True(WebAuthnSignCountPolicy.IsAcceptable(10, 11));
        Assert.True(WebAuthnSignCountPolicy.IsAcceptable(10, 10));
        Assert.True(WebAuthnSignCountPolicy.IsAcceptable(0, 0));
        Assert.False(WebAuthnSignCountPolicy.IsAcceptable(0, -1));
        Assert.False(WebAuthnSignCountPolicy.IsAcceptable(10, 9));
    }
}
