using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Viegard.AdminApi.Auth;
using Viegard.AdminApi.Signatures;
using Viegard.Application.Detection;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Configuration;
using Viegard.Domain.Events;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class AdminSignatureEndpointTests
{
    [Fact]
    public async Task Preview_does_not_require_step_up_and_does_not_write_audit()
    {
        var fixture = await EndpointFixture.CreateAsync();
        await fixture.Events.AddAsync(HttpEvent("/track?ref=%2Fpage%2F"));
        fixture.Context.Request.Form = ValidForm(
            pattern: "ref=",
            additionalPattern2: "%2Fpage%2F");

        var result = await AdminSignatureEndpoints.PreviewAsync(
            fixture.Context,
            fixture.Antiforgery,
            fixture.Events,
            fixture.Users,
            Options.Create(new DetectionOptions()));
        var location = await ExecuteRedirectAsync(result, fixture.Context);
        var query = ParseQuery(location);

        Assert.EndsWith("#add", location, StringComparison.Ordinal);
        Assert.Equal("1", query["preview"]);
        Assert.Equal("1", query["previewMatchCount"]);
        Assert.Equal("1", query["previewScanned"]);
        Assert.Equal("ContainsAll", query["formMatchType"]);
        Assert.Equal("%2Fpage%2F", query["formAdditionalPattern2"]);
        Assert.DoesNotContain("Step-up", location, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.AuditLedger.Records);
    }

    [Fact]
    public async Task Preview_surfaces_candidate_validation_errors_and_preserves_form_values()
    {
        var fixture = await EndpointFixture.CreateAsync();
        fixture.Context.Request.Form = ValidForm(name: "needs-pattern", pattern: "");

        var result = await AdminSignatureEndpoints.PreviewAsync(
            fixture.Context,
            fixture.Antiforgery,
            fixture.Events,
            fixture.Users,
            Options.Create(new DetectionOptions()));
        var location = await ExecuteRedirectAsync(result, fixture.Context);
        var query = ParseQuery(location);

        Assert.EndsWith("#add", location, StringComparison.Ordinal);
        Assert.Equal("needs-pattern", query["formName"]);
        Assert.Equal("", query["formPattern"]);
        Assert.Contains("Pattern is required.", query["error"], StringComparison.Ordinal);
        Assert.False(query.ContainsKey("preview"));
    }

    [Fact]
    public async Task Preview_scan_honors_event_cap()
    {
        var fixture = await EndpointFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i <= AdminSignatureEndpoints.PreviewEventScanLimit; i++)
        {
            var uri = i == AdminSignatureEndpoints.PreviewEventScanLimit
                ? "/track?match-me=true"
                : "/track?noise=true";
            await fixture.Events.AddAsync(HttpEvent(uri, now.AddSeconds(-i)));
        }

        fixture.Context.Request.Form = ValidForm(pattern: "match-me");

        var result = await AdminSignatureEndpoints.PreviewAsync(
            fixture.Context,
            fixture.Antiforgery,
            fixture.Events,
            fixture.Users,
            Options.Create(new DetectionOptions()));
        var location = await ExecuteRedirectAsync(result, fixture.Context);
        var query = ParseQuery(location);

        Assert.Equal(AdminSignatureEndpoints.PreviewEventScanLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), query["previewScanned"]);
        Assert.Equal("0", query["previewMatchCount"]);
        Assert.Equal("true", query["previewCapped"]);
    }

    [Fact]
    public async Task Preview_scanner_and_shared_matcher_return_same_verdict()
    {
        var events = new InMemoryEventStore();
        var matching = HttpEvent("/track?ref=%2Fpage%2F");
        var nonMatching = HttpEvent("/track?ref=aftership", matching.OccurredAt.AddSeconds(-1));
        await events.AddAsync(matching);
        await events.AddAsync(nonMatching);
        var signature = Signature(
            target: CustomSignatureTarget.HttpQuery,
            matchType: CustomSignatureMatchType.ContainsAll,
            pattern: "ref=",
            additionalPatterns: ["%2Fpage%2F"]);
        var options = new DetectionOptions();

        var preview = await AdminSignatureEndpoints.ScanPreviewAsync(events, signature, options);

        var expectedMatches = new[] { matching, nonMatching }
            .Count(candidateEvent => CustomSignatureMatcher.Matches(candidateEvent, signature, options));
        Assert.Equal(expectedMatches, preview.MatchCount);
        Assert.Equal(matching.Id, Assert.Single(preview.MatchingEventIds));
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

    private static FormCollection ValidForm(
        string id = "",
        string name = "preview-test",
        CustomSignatureTarget target = CustomSignatureTarget.HttpQuery,
        CustomSignatureMatchType matchType = CustomSignatureMatchType.Contains,
        string pattern = "ref=",
        string additionalPattern2 = "",
        string additionalPattern3 = "",
        string category = "test",
        int severity = 3,
        double evidenceWeight = 1.0,
        bool enabled = true) => new(
        new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["name"] = name,
            ["target"] = target.ToString(),
            ["matchType"] = matchType.ToString(),
            ["pattern"] = pattern,
            ["additionalPattern2"] = additionalPattern2,
            ["additionalPattern3"] = additionalPattern3,
            ["category"] = category,
            ["severity"] = severity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["evidenceWeight"] = evidenceWeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["enabled"] = enabled ? "true" : "false",
        });

    private static IReadOnlyDictionary<string, string> ParseQuery(string location)
    {
        var queryStart = location.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var fragmentStart = location.IndexOf('#', queryStart + 1);
        var query = fragmentStart < 0
            ? location[(queryStart + 1)..]
            : location[(queryStart + 1)..fragmentStart];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
            values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return values;
    }

    private static CustomSignature Signature(
        CustomSignatureTarget target,
        CustomSignatureMatchType matchType,
        string pattern,
        IReadOnlyList<string>? additionalPatterns = null) => new()
    {
        Id = ViegardId.New(),
        Name = "preview-shared",
        Enabled = true,
        Target = target,
        MatchType = matchType,
        Pattern = pattern,
        AdditionalPatterns = additionalPatterns ?? [],
        Category = "test",
        Severity = 3,
        EvidenceWeight = 1.0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
        Version = 1,
    };

    private static NormalizedEvent HttpEvent(string uri) => HttpEvent(uri, DateTimeOffset.UtcNow);

    private static NormalizedEvent HttpEvent(string uri, DateTimeOffset occurredAt) => new()
    {
        Id = ViegardId.New(),
        SourceId = "nginx-test",
        SourceType = "syslog",
        OccurredAt = occurredAt,
        Entities = [new EntityRef(EntityKind.IpAddress, "203.0.113.10")],
        Payload = new HttpRequestEvent
        {
            RemoteAddress = "203.0.113.10",
            Method = "GET",
            Uri = uri,
            Protocol = "HTTP/1.1",
            StatusCode = 200,
            UserAgent = "Mozilla/5.0",
            Host = "www.example.com",
        },
        RawObservationId = ViegardId.New(),
    };

    private sealed record EndpointFixture(
        DefaultHttpContext Context,
        InMemoryEventStore Events,
        InMemoryAdminUserStore Users,
        RecordingAuditLedger AuditLedger,
        NoopAntiforgery Antiforgery)
    {
        public static async Task<EndpointFixture> CreateAsync()
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
            var users = new InMemoryAdminUserStore();
            await users.CreateAsync(user);
            var context = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddLogging()
                    .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
                    .BuildServiceProvider(),
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                ], "test")),
            };
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/x-www-form-urlencoded";
            return new EndpointFixture(
                context,
                new InMemoryEventStore(),
                users,
                new RecordingAuditLedger(),
                new NoopAntiforgery());
        }
    }

    private sealed class RecordingAuditLedger
    {
        public List<object> Records { get; } = [];
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
