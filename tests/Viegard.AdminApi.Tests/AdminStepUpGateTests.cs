using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Viegard.AdminApi.Auth;
using Viegard.Application.Auth;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class AdminStepUpGateTests
{
    [Fact]
    public async Task Fresh_step_up_passes_and_renews_the_window()
    {
        var sessions = new InMemoryAdminSessionStore();
        var session = await CreateSessionAsync(sessions, stepUpAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var context = CreateContext(session.Id, validity: TimeSpan.FromMinutes(15));

        var fresh = await AdminStepUpGate.HasRecentStepUpAsync(context, sessions);

        Assert.True(fresh);
        var renewed = await sessions.GetAsync(session.Id);
        Assert.NotNull(renewed?.StepUpAt);
        Assert.True(renewed!.StepUpAt >= DateTimeOffset.UtcNow.AddSeconds(-5), "A passing gate check renews StepUpAt.");
    }

    [Fact]
    public async Task Stale_step_up_fails_and_does_not_renew()
    {
        var sessions = new InMemoryAdminSessionStore();
        var staleStepUpAt = DateTimeOffset.UtcNow.AddMinutes(-16);
        var session = await CreateSessionAsync(sessions, stepUpAt: staleStepUpAt);
        var context = CreateContext(session.Id, validity: TimeSpan.FromMinutes(15));

        var fresh = await AdminStepUpGate.HasRecentStepUpAsync(context, sessions);

        Assert.False(fresh);
        var unchanged = await sessions.GetAsync(session.Id);
        Assert.Equal(staleStepUpAt, unchanged!.StepUpAt);
    }

    [Fact]
    public async Task Session_without_step_up_fails()
    {
        var sessions = new InMemoryAdminSessionStore();
        var session = await CreateSessionAsync(sessions, stepUpAt: null);
        var context = CreateContext(session.Id, validity: TimeSpan.FromMinutes(15));

        Assert.False(await AdminStepUpGate.HasRecentStepUpAsync(context, sessions));
    }

    [Fact]
    public async Task Missing_session_claim_fails()
    {
        var sessions = new InMemoryAdminSessionStore();
        var context = CreateContext(sessionId: null, validity: TimeSpan.FromMinutes(15));

        Assert.False(await AdminStepUpGate.HasRecentStepUpAsync(context, sessions));
    }

    private static async Task<AdminSession> CreateSessionAsync(InMemoryAdminSessionStore sessions, DateTimeOffset? stepUpAt)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AdminSession
        {
            Id = ViegardId.New(),
            UserId = ViegardId.New(),
            CreatedAt = now.AddHours(-1),
            LastSeenAt = now,
            AbsoluteExpiresAt = now.AddDays(1),
            IdleExpiresAt = now.AddHours(1),
            Ip = "127.0.0.1",
            IpBindingMode = AdminIpBindingModes.Strict,
            UserAgent = "test",
            RevokedAt = null,
            StepUpAt = stepUpAt,
        };
        await sessions.CreateAsync(session);
        return session;
    }

    private static DefaultHttpContext CreateContext(Guid? sessionId, TimeSpan validity)
    {
        var services = new ServiceCollection()
            .AddSingleton<IOptions<AdminAuthOptions>>(Options.Create(new AdminAuthOptions { StepUpValidity = validity }))
            .BuildServiceProvider();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (sessionId is { } id)
        {
            claims.Add(new Claim(AdminCookieNames.SessionIdClaim, id.ToString()));
        }

        return new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
    }
}
