using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using Viegard.AdminApi.Auth;
using Viegard.Application.Auth;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class AppPasswordAuthenticationHandlerTests
{
    [Fact]
    public async Task Valid_token_authenticates_with_read_only_claims()
    {
        var harness = await HarnessAsync();

        var result = await harness.AuthenticateAsync("Bearer " + harness.Token);

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(harness.User.Id.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(harness.User.Username, principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(AppPasswordDefaults.AuthMethodValue, principal.FindFirstValue(AppPasswordDefaults.AuthMethodClaim));
        Assert.Equal(harness.AppPassword.Id.ToString(), principal.FindFirstValue(AppPasswordDefaults.TokenIdClaim));
        // No session claim: this principal can never satisfy the step-up gate.
        Assert.Null(principal.FindFirstValue(AdminCookieNames.SessionIdClaim));
        Assert.NotNull((await harness.Store.GetByLookupKeyAsync(harness.AppPassword.LookupKey))!.LastUsedAt);
    }

    [Fact]
    public async Task Missing_or_foreign_bearer_headers_yield_no_result()
    {
        var harness = await HarnessAsync();

        Assert.True((await harness.AuthenticateAsync(null)).None);
        Assert.True((await harness.AuthenticateAsync("Bearer some-jwt-token")).None);
        Assert.True((await harness.AuthenticateAsync("Basic dXNlcjpwYXNz")).None);
    }

    [Fact]
    public async Task Wrong_secret_with_known_lookup_key_fails()
    {
        var harness = await HarnessAsync();
        var forged = harness.Token[..^4] + "AAAA";

        var result = await harness.AuthenticateAsync("Bearer " + forged);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Token_without_networks_authenticates_from_any_address()
    {
        var harness = await HarnessAsync();

        Assert.True((await harness.AuthenticateAsync("Bearer " + harness.Token, "203.0.113.9")).Succeeded);
        Assert.True((await harness.AuthenticateAsync("Bearer " + harness.Token)).Succeeded);
    }

    [Theory]
    [InlineData("198.51.100.20", true)]
    [InlineData("192.0.2.77", true)]
    [InlineData("::ffff:192.0.2.77", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData(null, false)]
    public async Task Token_with_networks_is_enforced_against_the_remote_address(string? remote, bool allowed)
    {
        var harness = await HarnessAsync(allowedNetworks: ["198.51.100.20", "192.0.2.0/24"]);

        var result = await harness.AuthenticateAsync("Bearer " + harness.Token, remote);

        Assert.Equal(allowed, result.Succeeded);
        if (!allowed)
        {
            Assert.NotNull(result.Failure);
        }
    }

    [Fact]
    public async Task Token_with_unreadable_stored_network_fails_closed()
    {
        var harness = await HarnessAsync(allowedNetworks: ["not-a-network"]);

        Assert.False((await harness.AuthenticateAsync("Bearer " + harness.Token, "192.0.2.1")).Succeeded);
    }

    [Fact]
    public async Task Revoked_token_fails()
    {
        var harness = await HarnessAsync();
        await harness.Store.RevokeAsync(harness.AppPassword.Id, harness.User.Id, DateTimeOffset.UtcNow);

        var result = await harness.AuthenticateAsync("Bearer " + harness.Token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Expired_token_fails()
    {
        var harness = await HarnessAsync(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        var result = await harness.AuthenticateAsync("Bearer " + harness.Token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Locked_owner_fails()
    {
        var harness = await HarnessAsync(lockedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var result = await harness.AuthenticateAsync("Bearer " + harness.Token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Owner_in_remediation_state_fails()
    {
        var mustChange = await HarnessAsync(mustChangePassword: true);
        Assert.False((await mustChange.AuthenticateAsync("Bearer " + mustChange.Token)).Succeeded);

        var noTotp = await HarnessAsync(totpEnrolled: false);
        Assert.False((await noTotp.AuthenticateAsync("Bearer " + noTotp.Token)).Succeeded);
    }

    private sealed record Harness(
        AppPasswordAuthenticationHandler Handler,
        InMemoryAppPasswordStore Store,
        AdminUser User,
        AppPassword AppPassword,
        string Token)
    {
        public async Task<AuthenticateResult> AuthenticateAsync(string? authorizationHeader, string? remoteAddress = null)
        {
            var context = new DefaultHttpContext();
            if (remoteAddress is not null)
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
            }

            if (authorizationHeader is not null)
            {
                context.Request.Headers.Authorization = authorizationHeader;
            }

            await Handler.InitializeAsync(
                new AuthenticationScheme(
                    AppPasswordDefaults.SchemeName,
                    AppPasswordDefaults.SchemeName,
                    typeof(AppPasswordAuthenticationHandler)),
                context);
            return await Handler.AuthenticateAsync();
        }
    }

    private static async Task<Harness> HarnessAsync(
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? lockedUntil = null,
        bool mustChangePassword = false,
        bool totpEnrolled = true,
        string[]? allowedNetworks = null)
    {
        var now = DateTimeOffset.UtcNow;
        var users = new InMemoryAdminUserStore();
        var user = new AdminUser
        {
            Id = ViegardId.New(),
            Username = "hannah",
            PasswordHash = "hash",
            PasswordChangedAt = now,
            FailedLoginCount = 0,
            LockedUntil = lockedUntil,
            MustChangePassword = mustChangePassword,
            TotpEnrolled = totpEnrolled,
            CreatedAt = now,
        };
        await users.CreateAsync(user);

        var store = new InMemoryAppPasswordStore();
        var generated = AppPasswordTokenFormat.Generate();
        var appPassword = new AppPassword
        {
            Id = ViegardId.New(),
            UserId = user.Id,
            Name = "copilot-cli",
            LookupKey = generated.LookupKey,
            SecretHash = generated.SecretHash,
            CreatedAt = now,
            ExpiresAt = expiresAt ?? now.AddDays(90),
            AllowedSourceNetworks = allowedNetworks ?? [],
        };
        await store.CreateAsync(appPassword);

        var handler = new AppPasswordAuthenticationHandler(
            new StaticOptionsMonitor(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            store,
            users);
        return new Harness(handler, store, user, appPassword, generated.Token);
    }

    private sealed class StaticOptionsMonitor(AuthenticationSchemeOptions value)
        : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => value;

        public AuthenticationSchemeOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
