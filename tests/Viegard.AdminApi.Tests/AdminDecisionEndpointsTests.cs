using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Viegard.AdminApi.Auth;
using Viegard.AdminApi.Decisions;
using Viegard.Application.Audit;
using Viegard.Application.Auth;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Domain.Admin;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class AdminDecisionEndpointsTests
{
    [Fact]
    public async Task ReviewDecision_rejects_duration_outside_whitelist_without_claiming()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var decision = await fixture.AddDecisionChainAsync("ip=198.51.100.10");
        fixture.Context.Request.Form = ReviewForm(decision.Id, "approve", "5m");

        var result = await fixture.InvokeReviewAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Approval%20duration%20must%20be%201d%2C%207d%2C%20or%2030d", location, StringComparison.Ordinal);
        Assert.Null((await fixture.Decisions.GetAsync(decision.Id))!.ReviewedAt);
        Assert.Empty(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
    }

    [Fact]
    public async Task ReviewDecision_requires_step_up_before_claiming()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        var decision = await fixture.AddDecisionChainAsync("ip=198.51.100.10");
        fixture.Context.Request.Form = ReviewForm(decision.Id, "approve", "1d");

        var result = await fixture.InvokeReviewAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Null((await fixture.Decisions.GetAsync(decision.Id))!.ReviewedAt);
        Assert.Empty(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewDecision_rejects_underivable_target_without_claiming()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var decision = await fixture.AddDecisionChainAsync("source=missing-ip");
        fixture.Context.Request.Form = ReviewForm(decision.Id, "approve", "1d");

        var result = await fixture.InvokeReviewAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Approval%20unavailable%20for%20this%20decision", location, StringComparison.Ordinal);
        Assert.Null((await fixture.Decisions.GetAsync(decision.Id))!.ReviewedAt);
        Assert.Empty(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
    }

    [Fact]
    public async Task ReviewDecision_approves_claims_and_queues_ban_with_exact_parameters()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var decision = await fixture.AddDecisionChainAsync("ip=198.51.100.10|window=60s");
        fixture.Context.Request.Form = ReviewForm(decision.Id, "approve", "7d");

        var result = await fixture.InvokeReviewAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Decision%20approved", location, StringComparison.Ordinal);
        var reviewed = await fixture.Decisions.GetAsync(decision.Id);
        Assert.Equal(DecisionReviewOutcome.Approved, reviewed!.ReviewOutcome);
        Assert.Equal("hannah", reviewed.ReviewedBy);

        var action = Assert.Single(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
        Assert.Equal(decision.Id, action.DecisionId);
        Assert.Equal("ban-ip", action.OperationId);
        Assert.Equal(ActionStatus.Pending, action.Status);
        Assert.Equal("""{"ip":"198.51.100.10","timeout":"7d"}""", action.ParametersJson);
        Assert.Equal(1, (await fixture.ActionQueue.GetStatsAsync()).TotalEnqueued);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("Approved", StringComparison.Ordinal)
            && record.ActionId == action.Id);
    }

    [Fact]
    public async Task ReviewDecision_double_review_loses_claim_and_does_not_queue_second_action()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var decision = await fixture.AddDecisionChainAsync("ip=198.51.100.10");
        fixture.Context.Request.Form = ReviewForm(decision.Id, "approve", "1d");
        _ = await ExecuteRedirectAsync(await fixture.InvokeReviewAsync(), fixture.Context);
        fixture.Context.Response.Body = Stream.Null;
        fixture.Context.Response.Headers.Clear();
        fixture.Context.Request.Form = ReviewForm(decision.Id, "reject", null);

        var second = await fixture.InvokeReviewAsync();
        var location = await ExecuteRedirectAsync(second, fixture.Context);

        Assert.Contains("already%20been%20reviewed", location, StringComparison.Ordinal);
        Assert.Equal(DecisionReviewOutcome.Approved, (await fixture.Decisions.GetAsync(decision.Id))!.ReviewOutcome);
        Assert.Single(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
    }

    [Fact]
    public async Task ReviewDecision_rejects_claims_without_queueing_action()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var decision = await fixture.AddDecisionChainAsync("source=not-needed-for-reject");
        fixture.Context.Request.Form = ReviewForm(decision.Id, "reject", null);

        var result = await fixture.InvokeReviewAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Decision%20rejected", location, StringComparison.Ordinal);
        Assert.Equal(DecisionReviewOutcome.Rejected, (await fixture.Decisions.GetAsync(decision.Id))!.ReviewOutcome);
        Assert.Empty(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
    }

    [Fact]
    public async Task BulkReject_requires_step_up_before_rejecting()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        var decision = await fixture.AddDecisionChainAsync("ip=198.51.100.10", severity: 2);
        fixture.Context.Request.Form = BulkRejectForm("3");

        var result = await fixture.InvokeBulkRejectAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Null((await fixture.Decisions.GetAsync(decision.Id))!.ReviewedAt);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkReject_rejects_invalid_max_severity()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var decision = await fixture.AddDecisionChainAsync("ip=198.51.100.10", severity: 2);

        foreach (var invalid in new[] { "", "0", "11", "abc" })
        {
            fixture.Context.Response.Body = Stream.Null;
            fixture.Context.Response.Headers.Clear();
            fixture.Context.Request.Form = BulkRejectForm(invalid);

            var result = await fixture.InvokeBulkRejectAsync();
            var location = await ExecuteRedirectAsync(result, fixture.Context);

            Assert.Contains("Max%20severity%20must%20be%20a%20number%20between%201%20and%2010", location, StringComparison.Ordinal);
        }

        Assert.Null((await fixture.Decisions.GetAsync(decision.Id))!.ReviewedAt);
    }

    [Fact]
    public async Task BulkReject_rejects_only_low_severity_unreviewed_decisions_and_audits()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var low = await fixture.AddDecisionChainAsync("ip=198.51.100.10", severity: 1);
        var alsoLow = await fixture.AddDecisionChainAsync("ip=198.51.100.11", severity: 3);
        var high = await fixture.AddDecisionChainAsync("ip=198.51.100.12", severity: 8);
        fixture.Context.Request.Form = BulkRejectForm("3");

        var result = await fixture.InvokeBulkRejectAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Rejected%202%20decisions%20at%20severity%203%20or%20below", location, StringComparison.Ordinal);
        Assert.Equal(DecisionReviewOutcome.Rejected, (await fixture.Decisions.GetAsync(low.Id))!.ReviewOutcome);
        Assert.Equal(DecisionReviewOutcome.Rejected, (await fixture.Decisions.GetAsync(alsoLow.Id))!.ReviewOutcome);
        Assert.Equal("hannah", (await fixture.Decisions.GetAsync(low.Id))!.ReviewedBy);
        Assert.Null((await fixture.Decisions.GetAsync(high.Id))!.ReviewedAt);
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("bulk-rejected 2 unreviewed decisions at severity 3 or below", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkReject_reports_zero_matches_without_error()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var high = await fixture.AddDecisionChainAsync("ip=198.51.100.12", severity: 8);
        fixture.Context.Request.Form = BulkRejectForm("3");

        var result = await fixture.InvokeBulkRejectAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("No%20unreviewed%20decisions%20at%20severity%203%20or%20below", location, StringComparison.Ordinal);
        Assert.Null((await fixture.Decisions.GetAsync(high.Id))!.ReviewedAt);
    }

    [Fact]
    public async Task Unban_rejects_invalid_ip_without_action()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = UnbanForm("not-an-ip");

        var result = await fixture.InvokeUnbanAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Ban%20IP%20was%20not%20valid", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
    }

    [Fact]
    public async Task Unban_requires_step_up_before_queueing_action()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: false);
        var originalDecisionId = ViegardId.New();
        await fixture.ActiveBans.UpsertByIpAsync(new ActiveBan
        {
            Id = ViegardId.New(),
            Ip = "203.0.113.10",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            DecisionId = originalDecisionId,
            ActionId = ViegardId.New(),
        });
        fixture.Context.Request.Form = UnbanForm("203.0.113.10");

        var result = await fixture.InvokeUnbanAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Step-up%20verification%20is%20required", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
        Assert.Contains(fixture.AuditLedger.Records, record =>
            record.Summary.Contains("StepUpFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unban_rejects_when_no_active_ban_exists()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        fixture.Context.Request.Form = UnbanForm("203.0.113.10");

        var result = await fixture.InvokeUnbanAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("No%20active%20ban%20exists", location, StringComparison.Ordinal);
        Assert.Empty(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
    }

    [Fact]
    public async Task Unban_queues_remove_ban_with_original_decision_id()
    {
        var fixture = await EndpointFixture.CreateAsync(freshStepUp: true);
        var originalDecisionId = ViegardId.New();
        await fixture.ActiveBans.UpsertByIpAsync(new ActiveBan
        {
            Id = ViegardId.New(),
            Ip = "203.0.113.10",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            DecisionId = originalDecisionId,
            ActionId = ViegardId.New(),
        });
        fixture.Context.Request.Form = UnbanForm("203.0.113.10");

        var result = await fixture.InvokeUnbanAsync();
        var location = await ExecuteRedirectAsync(result, fixture.Context);

        Assert.Contains("Unban%20action", location, StringComparison.Ordinal);
        var action = Assert.Single(await fixture.Actions.ListRecentByProviderAsync("mikrotik", 10));
        Assert.Equal(originalDecisionId, action.DecisionId);
        Assert.Equal("remove-ban", action.OperationId);
        Assert.Equal("""{"ip":"203.0.113.10"}""", action.ParametersJson);
        Assert.Equal(1, (await fixture.ActionQueue.GetStatsAsync()).TotalEnqueued);
    }

    private static async Task<string> ExecuteRedirectAsync(IResult result, HttpContext context)
    {
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        var location = context.Response.Headers.Location.ToString();

        // Banner messages travel in the protected flash cookie instead of the
        // query string.  Re-synthesize the legacy query form so assertions
        // keep verifying the exact user-visible message text.
        var flash = ReadFlashMessage(context);
        if (flash is null)
        {
            return location;
        }

        var key = flash.IsError ? "error" : "status";
        var fragmentStart = location.IndexOf('#', StringComparison.Ordinal);
        var basePath = fragmentStart < 0 ? location : location[..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : location[fragmentStart..];
        var separator = basePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{basePath}{separator}{key}={Uri.EscapeDataString(flash.Message)}{fragment}";
    }

    private static AdminFlashMessage? ReadFlashMessage(HttpContext context)
    {
        var setCookie = context.Response.Headers.SetCookie
            .FirstOrDefault(value => value?.StartsWith(AdminFlashMessages.CookieName + "=", StringComparison.Ordinal) == true);
        if (setCookie is null)
        {
            return null;
        }

        var reader = new DefaultHttpContext { RequestServices = context.RequestServices };
        reader.Request.Headers.Cookie = setCookie.Split(';')[0];
        return AdminFlashMessages.Consume(reader);
    }

    private static FormCollection ReviewForm(Guid id, string verdict, string? duration) =>
        Form(("id", id.ToString("N")), ("verdict", verdict), ("duration", duration ?? string.Empty));

    private static FormCollection UnbanForm(string ip) =>
        Form(("ip", ip));

    private static FormCollection BulkRejectForm(string maxSeverity) =>
        Form(("maxSeverity", maxSeverity));

    private static FormCollection Form(params (string Key, string Value)[] pairs) =>
        new(pairs.ToDictionary(
            pair => pair.Key,
            pair => new StringValues(pair.Value),
            StringComparer.Ordinal));

    private sealed record EndpointFixture(
        DefaultHttpContext Context,
        InMemoryDecisionStore Decisions,
        InMemoryClassificationStore Classifications,
        InMemoryIncidentStore Incidents,
        InMemoryActionStore Actions,
        InMemoryActiveBanStore ActiveBans,
        ChannelWorkQueue<ActionWorkItem> ActionQueue,
        RecordingAuditLedger AuditLedger,
        NoopAntiforgery Antiforgery,
        InMemoryAdminUserStore Users,
        InMemoryAdminSessionStore Sessions,
        AdminAuthAuditor AuthAuditor,
        AdminConfigAuditor ConfigAuditor,
        DecisionTargetResolver TargetResolver)
    {
        public static async Task<EndpointFixture> CreateAsync(bool freshStepUp)
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

            var classifications = new InMemoryClassificationStore();
            var decisions = new InMemoryDecisionStore(classifications);
            var incidents = new InMemoryIncidentStore();
            var actions = new InMemoryActionStore();
            var activeBans = new InMemoryActiveBanStore();
            var actionQueue = new ChannelWorkQueue<ActionWorkItem>("actions");
            var eventQueue = new ChannelWorkQueue<Guid>("events");
            var auditLedger = new RecordingAuditLedger();
            var authAuditor = new AdminAuthAuditor(
                auditLedger,
                new InMemoryRawObservationStore(),
                new InMemoryEventStore(),
                eventQueue,
                NullLogger<AdminAuthAuditor>.Instance);
            var configAuditor = new AdminConfigAuditor(auditLedger, NullLogger<AdminConfigAuditor>.Instance);
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IOptions<AdminAuthOptions>>(Options.Create(new AdminAuthOptions()))
                .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
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

            return new EndpointFixture(
                context,
                decisions,
                classifications,
                incidents,
                actions,
                activeBans,
                actionQueue,
                auditLedger,
                new NoopAntiforgery(),
                users,
                sessions,
                authAuditor,
                configAuditor,
                new DecisionTargetResolver(classifications, incidents));
        }

        public async Task<Decision> AddDecisionChainAsync(string correlationKey, int severity = 7)
        {
            var incident = new Incident
            {
                Id = ViegardId.New(),
                CorrelationKey = correlationKey,
                WindowStart = DateTimeOffset.UtcNow.AddMinutes(-5),
                WindowEnd = DateTimeOffset.UtcNow,
                EventIds = [],
                Evidence = [],
                State = IncidentState.Classified,
            };
            var classification = new Classification
            {
                Id = ViegardId.New(),
                SubjectKind = ClassificationSubjectKind.Incident,
                SubjectId = incident.Id,
                ClassifierId = "test-classifier",
                Category = "scanner",
                Confidence = 0.8,
                Severity = severity,
                Reasons = ["test"],
                RecommendedAction = "temp-ban-ip",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var decision = new Decision
            {
                Id = ViegardId.New(),
                ClassificationId = classification.Id,
                PolicyId = "viegard-default",
                PolicyVersion = "1",
                Outcome = DecisionOutcome.RequireApproval,
                Rationale = "requires approval",
                Guardrails = [],
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await Incidents.UpsertAsync(incident);
            await Classifications.AddAsync(classification);
            await Decisions.AddAsync(decision);
            return decision;
        }

        public Task<IResult> InvokeReviewAsync() =>
            AdminDecisionEndpoints.ReviewDecisionAsync(
                Context,
                Antiforgery,
                Decisions,
                Classifications,
                Incidents,
                TargetResolver,
                Actions,
                ActionQueue,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeUnbanAsync() =>
            AdminDecisionEndpoints.UnbanAsync(
                Context,
                Antiforgery,
                ActiveBans,
                Actions,
                ActionQueue,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);

        public Task<IResult> InvokeBulkRejectAsync() =>
            AdminDecisionEndpoints.BulkRejectAsync(
                Context,
                Antiforgery,
                Decisions,
                Users,
                Sessions,
                AuthAuditor,
                ConfigAuditor);
    }

    private sealed class RecordingAuditLedger : IAuditLedger
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
