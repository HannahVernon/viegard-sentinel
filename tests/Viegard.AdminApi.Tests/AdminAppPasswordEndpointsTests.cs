using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Viegard.AdminApi.Auth;
using Viegard.Application.Audit;
using Viegard.Application.Auth;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class AdminAppPasswordEndpointsTests
{
    [Fact]
    public async Task Create_requires_step_up()
    {
        var fixture = await FixtureAsync(freshStepUp: false);
        fixture.Context.Request.Form = Form(("name", "copilot-cli"));

        var result = await fixture.InvokeCreateAsync();
        var outcome = await fixture.ExecuteAsync(result);

        Assert.Contains("Step-up verification is required", outcome.Error);
        Assert.Empty(await fixture.AppPasswords.ListForUserAsync(fixture.User.Id));
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_missing_name(string name)
    {
        var fixture = await FixtureAsync(freshStepUp: true);
        fixture.Context.Request.Form = Form(("name", name));

        var result = await fixture.InvokeCreateAsync();
        var outcome = await fixture.ExecuteAsync(result);

        Assert.Contains("name is required", outcome.Error);
        Assert.Empty(await fixture.AppPasswords.ListForUserAsync(fixture.User.Id));
    }

    [Fact]
    public async Task Create_stores_hash_delivers_token_once_and_audits()
    {
        var fixture = await FixtureAsync(freshStepUp: true);
        fixture.Context.Request.Form = Form(("name", "copilot-cli"));

        var result = await fixture.InvokeCreateAsync();
        var outcome = await fixture.ExecuteAsync(result);

        Assert.Equal("/account#app-passwords", outcome.Location);
        Assert.Contains("shown once", outcome.Status);

        var stored = Assert.Single(await fixture.AppPasswords.ListForUserAsync(fixture.User.Id));
        Assert.Equal("copilot-cli", stored.Name);
        Assert.NotNull(stored.ExpiresAt);
        Assert.InRange(
            (stored.ExpiresAt!.Value - DateTimeOffset.UtcNow.AddDays(90)).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1));

        var token = fixture.ReadNewTokenCookie();
        Assert.NotNull(token);
        Assert.True(AppPasswordTokenFormat.TryParseLookupKey(token!, out var lookupKey));
        Assert.Equal(stored.LookupKey, lookupKey);
        Assert.True(AppPasswordTokenFormat.Matches(token!, stored.SecretHash));

        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("created read-only app password 'copilot-cli'", StringComparison.Ordinal));
        // The plaintext token never reaches the audit ledger.
        Assert.DoesNotContain(fixture.AuditLedger.Records, record =>
            record.Summary.Contains(token!, StringComparison.Ordinal)
            || record.DetailJson?.Contains(token!, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Revoke_requires_step_up_and_valid_id()
    {
        var gated = await FixtureAsync(freshStepUp: false);
        gated.Context.Request.Form = Form(("id", Guid.NewGuid().ToString()));
        Assert.Contains(
            "Step-up verification is required",
            (await gated.ExecuteAsync(await gated.InvokeRevokeAsync())).Error);

        var fixture = await FixtureAsync(freshStepUp: true);
        fixture.Context.Request.Form = Form(("id", "not-a-guid"));
        Assert.Contains(
            "id was not valid",
            (await fixture.ExecuteAsync(await fixture.InvokeRevokeAsync())).Error);
    }

    [Fact]
    public async Task Revoke_marks_token_revoked_and_audits()
    {
        var fixture = await FixtureAsync(freshStepUp: true);
        var generated = AppPasswordTokenFormat.Generate();
        var appPassword = new AppPassword
        {
            Id = ViegardId.New(),
            UserId = fixture.User.Id,
            Name = "copilot-cli",
            LookupKey = generated.LookupKey,
            SecretHash = generated.SecretHash,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
        };
        await fixture.AppPasswords.CreateAsync(appPassword);
        fixture.Context.Request.Form = Form(("id", appPassword.Id.ToString()));

        var outcome = await fixture.ExecuteAsync(await fixture.InvokeRevokeAsync());

        Assert.Contains("App password revoked", outcome.Status);
        Assert.NotNull((await fixture.AppPasswords.GetByLookupKeyAsync(generated.LookupKey))!.RevokedAt);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("revoked read-only app password", StringComparison.Ordinal));

        fixture.Context.Response.Body = Stream.Null;
        fixture.Context.Response.Headers.Clear();
        Assert.Contains(
            "already revoked",
            (await fixture.ExecuteAsync(await fixture.InvokeRevokeAsync())).Error);
    }

    private static FormCollection Form(params (string Key, string Value)[] pairs) =>
        new(pairs.ToDictionary(
            pair => pair.Key,
            pair => new StringValues(pair.Value),
            StringComparer.Ordinal));

    private sealed record Outcome(string Location, string Status, string Error);

    private sealed record Fixture(
        DefaultHttpContext Context,
        InMemoryAppPasswordStore AppPasswords,
        InMemoryAdminUserStore Users,
        InMemoryAdminSessionStore Sessions,
        AdminUser User,
        NewAppPasswordCookie NewTokenCookie,
        AdminAuthAuditor AuthAuditor,
        AdminConfigAuditor ConfigAuditor,
        RecordingLedger AuditLedger)
    {
        public Task<IResult> InvokeCreateAsync() =>
            AdminAppPasswordEndpoints.CreateAsync(
                Context,
                new NoopAntiforgery(),
                AppPasswords,
                Users,
                Sessions,
                Options.Create(new AdminAuthOptions()),
                NewTokenCookie,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeRevokeAsync() =>
            AdminAppPasswordEndpoints.RevokeAsync(
                Context,
                new NoopAntiforgery(),
                AppPasswords,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public async Task<Outcome> ExecuteAsync(IResult result)
        {
            await result.ExecuteAsync(Context);
            var flash = ConsumeFlash();
            return new Outcome(
                Context.Response.Headers.Location.ToString(),
                flash is { IsError: false } ? flash.Message : string.Empty,
                flash is { IsError: true } ? flash.Message : string.Empty);
        }

        public string? ReadNewTokenCookie()
        {
            var reader = ReaderContext(AdminCookieNames.NewAppPassword);
            return reader is null ? null : NewTokenCookie.ReadAndClear(reader);
        }

        private AdminFlashMessage? ConsumeFlash()
        {
            var reader = ReaderContext(AdminFlashMessages.CookieName);
            return reader is null ? null : AdminFlashMessages.Consume(reader);
        }

        private DefaultHttpContext? ReaderContext(string cookieName)
        {
            var setCookie = Context.Response.Headers.SetCookie
                .FirstOrDefault(value => value?.StartsWith(cookieName + "=", StringComparison.Ordinal) == true);
            if (setCookie is null)
            {
                return null;
            }

            var reader = new DefaultHttpContext { RequestServices = Context.RequestServices };
            reader.Request.Headers.Cookie = setCookie.Split(';')[0];
            return reader;
        }
    }

    private static async Task<Fixture> FixtureAsync(bool freshStepUp)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new AdminUser
        {
            Id = ViegardId.New(),
            Username = "hannah",
            PasswordHash = "hash",
            PasswordChangedAt = now,
            FailedLoginCount = 0,
            LockedUntil = null,
            MustChangePassword = false,
            TotpEnrolled = true,
            CreatedAt = now,
        };
        var session = new AdminSession
        {
            Id = ViegardId.New(),
            UserId = user.Id,
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiresAt = now.AddDays(1),
            IdleExpiresAt = now.AddHours(1),
            Ip = "127.0.0.1",
            IpBindingMode = AdminIpBindingModes.Strict,
            UserAgent = "test",
            RevokedAt = null,
            StepUpAt = freshStepUp ? now : null,
        };
        var users = new InMemoryAdminUserStore();
        var sessions = new InMemoryAdminSessionStore();
        await users.CreateAsync(user);
        await sessions.CreateAsync(session);

        var ledger = new RecordingLedger();
        var authAuditor = new AdminAuthAuditor(
            ledger,
            new InMemoryRawObservationStore(),
            new InMemoryEventStore(),
            new ChannelWorkQueue<Guid>("events"),
            NullLogger<AdminAuthAuditor>.Instance);
        var configAuditor = new AdminConfigAuditor(ledger, NullLogger<AdminConfigAuditor>.Instance);
        var dataProtection = new EphemeralDataProtectionProvider();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IDataProtectionProvider>(dataProtection)
            .AddSingleton<IOptions<SessionSecurityOptions>>(Options.Create(new SessionSecurityOptions()))
            .AddSingleton(new SessionSecuritySettingsSource())
            .AddSingleton<IPendingStepUpActionStore, InMemoryPendingStepUpActionStore>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(AdminCookieNames.SessionIdClaim, session.Id.ToString()),
            ], "test")),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/x-www-form-urlencoded";

        return new Fixture(
            context,
            new InMemoryAppPasswordStore(),
            users,
            sessions,
            user,
            new NewAppPasswordCookie(dataProtection),
            authAuditor,
            configAuditor,
            ledger);
    }

    private sealed class RecordingLedger : IAuditLedger
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<KeysetPage<AuditRecord>> ListPageAsync(
            Guid? beforeId,
            int pageSize,
            AuditListFilter? filter = null,
            ListSort<AuditSortColumn>? sort = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KeysetPage<AuditRecord>(Records, null, Records.Count, 0));

        public ValueTask<Guid?> GetPageCursorAsync(
            int pageNumber,
            int pageSize,
            AuditListFilter? filter = null,
            ListSort<AuditSortColumn>? sort = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Guid?>(null);
    }

    private sealed class NoopAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            Task.CompletedTask;
    }
}
