using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Actions.MikroTik;
using Viegard.Application.Actions;
using Viegard.Application.Configuration;
using Viegard.Application.Policy;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class MikroTikActionProviderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Ban_ip_rejects_invalid_ip_without_network_io()
    {
        var fixture = new ProviderFixture();
        await fixture.AddRouterAsync("router-a");

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "not-an-ip", timeout = "5m" }));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("ip", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task Ban_ip_refuses_protected_range_without_network_io()
    {
        var fixture = new ProviderFixture(protectedCidrs: ["203.0.113.0/24"]);
        await fixture.AddRouterAsync("router-a");

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("D-0026 protected-address guard", result.Error, StringComparison.Ordinal);
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData("4m59s")]
    [InlineData("31d")]
    [InlineData("1m1h")]
    [InlineData("abc")]
    public async Task Ban_ip_rejects_timeout_outside_bounds_or_shape(string timeout)
    {
        var fixture = new ProviderFixture();
        await fixture.AddRouterAsync("router-a");

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout }));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("timeout", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(30, 0, 0, 0, "30d")]
    [InlineData(7, 0, 0, 0, "7d")]
    [InlineData(1, 0, 0, 0, "1d")]
    [InlineData(0, 1, 30, 0, "1h30m")]
    [InlineData(0, 0, 5, 0, "5m")]
    [InlineData(0, 0, 5, 12, "5m12s")]
    public void Format_routeros_duration_emits_non_zero_components_in_order(
        int days,
        int hours,
        int minutes,
        int seconds,
        string expected)
    {
        var duration = TimeSpan.FromDays(days)
            + TimeSpan.FromHours(hours)
            + TimeSpan.FromMinutes(minutes)
            + TimeSpan.FromSeconds(seconds);

        Assert.Equal(expected, MikroTikBanActionProvider.FormatRouterOsDuration(duration));
    }

    [Fact]
    public async Task Ban_ip_generates_server_side_comment_and_canonical_parameters()
    {
        var fixture = new ProviderFixture();
        var router = await fixture.AddRouterAsync("router-a");
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "{}"));
        var decisionId = Guid.Parse("018f6ad8-98e8-7b71-a62c-2f41829f2e41");

        var result = await fixture.Provider.ExecuteAsync(
            Action(
                MikroTikBanActionProvider.BanIpOperationId,
                new { ip = "203.0.113.10", timeout = "90m", comment = "attacker text" },
                decisionId: decisionId));

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Http.Requests).Body!);
        Assert.Equal("viegard decision 018f6ad898e87b71a62c2f41829f2e41", body.RootElement.GetProperty("comment").GetString());
        Assert.Equal("1h30m", body.RootElement.GetProperty("timeout").GetString());
        using var parameters = JsonDocument.Parse(result.ParametersJson!);
        Assert.Equal("viegard decision 018f6ad898e87b71a62c2f41829f2e41", parameters.RootElement.GetProperty("comment").GetString());
        Assert.DoesNotContain("attacker text", result.ParametersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ban_ip_persists_active_ban_and_uses_remaining_router_timeout()
    {
        var fixture = new ProviderFixture(advanceAfterActiveBanUpsert: TimeSpan.FromSeconds(31));
        var router = await fixture.AddRouterAsync("router-a");
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "{}"));
        var startedAt = fixture.Time.GetUtcNow();
        var decisionId = Guid.Parse("018f6ad8-98e8-7b71-a62c-2f41829f2e41");
        var action = Action(
            MikroTikBanActionProvider.BanIpOperationId,
            new { ip = "203.0.113.10", timeout = "5m" },
            decisionId);

        var result = await fixture.Provider.ExecuteAsync(action);

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        var activeBan = await fixture.ActiveBanRows.GetByIpAsync("203.0.113.10");
        Assert.NotNull(activeBan);
        Assert.Equal(startedAt.AddMinutes(5), activeBan.ExpiresAt);
        Assert.Equal(decisionId, activeBan.DecisionId);
        Assert.Equal(action.Id, activeBan.ActionId);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Http.Requests).Body!);
        Assert.Equal("4m29s", body.RootElement.GetProperty("timeout").GetString());
    }

    [Fact]
    public async Task Ban_ip_near_expiry_records_applied_without_router_call()
    {
        var fixture = new ProviderFixture(advanceAfterActiveBanUpsert: TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(1)));
        await fixture.AddRouterAsync("router-a");

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        Assert.Empty(fixture.Http.Requests);
        Assert.Equal(0, fixture.Credentials.UnprotectCalls);
        var routerResult = Assert.Single(Results(result));
        Assert.Equal("applied-without-call", routerResult.Status);
        Assert.Contains("about to expire", routerResult.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ban_ip_fans_out_and_succeeds_only_when_all_routers_apply()
    {
        var fixture = new ProviderFixture();
        var routerA = await fixture.AddRouterAsync("router-a");
        var routerB = await fixture.AddRouterAsync("router-b");
        fixture.Http.Enqueue(routerA.Id, JsonResponse(HttpStatusCode.OK, "{}"));
        fixture.Http.Enqueue(routerB.Id, JsonResponse(HttpStatusCode.Created, "{}"));

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        Assert.Equal(2, fixture.Http.Requests.Count);
        Assert.All(Results(result), item =>
        {
            Assert.Equal("applied", item.Status);
            Assert.Equal(1, item.Attempts);
        });
    }

    [Fact]
    public async Task Ban_ip_fails_when_no_routers_are_enabled()
    {
        var fixture = new ProviderFixture();
        await fixture.AddRouterAsync("router-a", enabled: false);

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("zero enabled routers", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task Ban_ip_partial_failure_stays_pending_with_per_router_results()
    {
        var fixture = new ProviderFixture();
        var routerA = await fixture.AddRouterAsync("router-a");
        var routerB = await fixture.AddRouterAsync("router-b");
        fixture.Http.Enqueue(routerA.Id, JsonResponse(HttpStatusCode.OK, "{}"));
        fixture.Http.Enqueue(routerB.Id, JsonResponse(HttpStatusCode.InternalServerError, "{}"));

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Pending, result.Status);
        var results = Results(result).OrderBy(item => item.RouterName, StringComparer.Ordinal).ToList();
        Assert.Equal("applied", results[0].Status);
        Assert.Equal("failed", results[1].Status);
        Assert.Equal(1, results[1].Attempts);
        Assert.Contains("retry pending", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ban_ip_duplicate_entry_is_applied_for_idempotency()
    {
        var fixture = new ProviderFixture();
        var router = await fixture.AddRouterAsync("router-a");
        fixture.Http.Enqueue(
            router.Id,
            JsonResponse(HttpStatusCode.BadRequest, "{\"message\":\"Already have such entry\"}"));

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        var item = Assert.Single(Results(result));
        Assert.Equal("applied", item.Status);
        Assert.Contains("duplicate", item.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_ban_deletes_every_matching_entry()
    {
        var fixture = new ProviderFixture();
        var router = await fixture.AddRouterAsync("router-a");
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, """[{".id":"*1"},{".id":"*2"}]"""));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "{}"));
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.NoContent, string.Empty));

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.RemoveBanOperationId, new { ip = "203.0.113.10" }));

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        Assert.Equal(["GET", "DELETE", "DELETE"], fixture.Http.Requests.Select(r => r.Method).ToArray());
        Assert.EndsWith("/%2A1", fixture.Http.Requests[1].Url, StringComparison.Ordinal);
        Assert.EndsWith("/%2A2", fixture.Http.Requests[2].Url, StringComparison.Ordinal);
        Assert.Contains("Removed 2", Assert.Single(Results(result)).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_ban_removes_active_ban_before_router_fanout()
    {
        var fixture = new ProviderFixture();
        var router = await fixture.AddRouterAsync("router-a");
        await fixture.ActiveBanRows.UpsertByIpAsync(new ActiveBan
        {
            Id = ViegardId.New(),
            Ip = "203.0.113.10",
            CreatedAt = fixture.Time.GetUtcNow(),
            ExpiresAt = fixture.Time.GetUtcNow().AddMinutes(5),
            DecisionId = ViegardId.New(),
            ActionId = ViegardId.New(),
        });
        fixture.Http.BeforeRequestAsync = async _ =>
        {
            Assert.Null(await fixture.ActiveBanRows.GetByIpAsync("203.0.113.10"));
        };
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "[]"));

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.RemoveBanOperationId, new { ip = "203.0.113.10" }));

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        Assert.Null(await fixture.ActiveBanRows.GetByIpAsync("203.0.113.10"));
        Assert.Single(fixture.Http.Requests);
    }

    [Fact]
    public async Task Remove_ban_zero_entries_is_applied_for_idempotency()
    {
        var fixture = new ProviderFixture();
        var router = await fixture.AddRouterAsync("router-a");
        fixture.Http.Enqueue(router.Id, JsonResponse(HttpStatusCode.OK, "[]"));

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.RemoveBanOperationId, new { ip = "203.0.113.10" }));

        Assert.Equal(ActionStatus.Succeeded, result.Status);
        Assert.Single(fixture.Http.Requests);
        Assert.Contains("No matching", Assert.Single(Results(result)).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_composes_exact_calls_without_network_or_decrypting_credentials()
    {
        var fixture = new ProviderFixture(dryRun: true);
        var router = await fixture.AddRouterAsync("router-a", password: "super-secret-password");

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.DryRun, result.Status);
        Assert.Empty(fixture.Http.Requests);
        Assert.Equal(0, fixture.Credentials.UnprotectCalls);
        Assert.Null(await fixture.ActiveBanRows.GetByIpAsync("203.0.113.10"));
        var detail = Assert.Single(Results(result)).Detail;
        using var calls = JsonDocument.Parse(detail);
        var call = Assert.Single(calls.RootElement.EnumerateArray());
        Assert.Equal("PUT", call.GetProperty("method").GetString());
        Assert.Equal($"{router.BaseUrl}/rest/ip/firewall/address-list", call.GetProperty("url").GetString());
        using var body = JsonDocument.Parse(call.GetProperty("body").GetString()!);
        Assert.Equal("viegard-banned", body.RootElement.GetProperty("list").GetString());
        Assert.Equal("203.0.113.10", body.RootElement.GetProperty("address").GetString());
        Assert.DoesNotContain("super-secret-password", result.ResultsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emergency_stop_refuses_execution_without_network_io()
    {
        var fixture = new ProviderFixture(emergencyStop: true);
        await fixture.AddRouterAsync("router-a");

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("EmergencyStop", result.Error, StringComparison.Ordinal);
        Assert.Empty(fixture.Http.Requests);
        Assert.Null(await fixture.ActiveBanRows.GetByIpAsync("203.0.113.10"));
    }

    [Fact]
    public async Task Credential_values_never_appear_in_results_or_errors()
    {
        var fixture = new ProviderFixture();
        var router = await fixture.AddRouterAsync("router-a", password: "super-secret-password");
        fixture.Http.Enqueue(
            router.Id,
            JsonResponse(HttpStatusCode.InternalServerError, "super-secret-password"));

        var result = await fixture.Provider.ExecuteAsync(
            Action(MikroTikBanActionProvider.BanIpOperationId, new { ip = "203.0.113.10", timeout = "5m" }));

        Assert.Equal(ActionStatus.Pending, result.Status);
        Assert.DoesNotContain("super-secret-password", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-password", result.ResultsJson, StringComparison.Ordinal);
    }

    internal static ActionRecord Action(string operationId, object parameters, Guid? decisionId = null) => new()
    {
        Id = ViegardId.New(),
        DecisionId = decisionId ?? ViegardId.New(),
        ProviderId = MikroTikBanActionProvider.MikroTikProviderId,
        OperationId = operationId,
        ParametersJson = JsonSerializer.Serialize(parameters, Json),
        Status = ActionStatus.Pending,
        RequestedAt = DateTimeOffset.UtcNow,
    };

    internal static IReadOnlyList<MikroTikRouterActionResult> Results(ActionRecord record) =>
        JsonSerializer.Deserialize<List<MikroTikRouterActionResult>>(record.ResultsJson ?? "[]", Json) ?? [];

    internal static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

internal sealed class ProviderFixture
{
    public ProviderFixture(
        bool dryRun = false,
        bool emergencyStop = false,
        IEnumerable<string>? protectedCidrs = null,
        TimeSpan? advanceAfterActiveBanUpsert = null)
    {
        ActiveBans = advanceAfterActiveBanUpsert is null
            ? ActiveBanRows
            : new AdvancingActiveBanStore(ActiveBanRows, Time, advanceAfterActiveBanUpsert.Value);
        var posture = new PolicyPostureOptions
        {
            DryRun = dryRun,
            EmergencyStop = emergencyStop,
            ManualApprovalMode = true,
        };
        Provider = new MikroTikBanActionProvider(
            Routers,
            ActiveBans,
            Credentials,
            new ProtectedAddressList(protectedCidrs ?? []),
            Options.Create(new PolicyOptions { Posture = posture }),
            Http,
            Time);
    }

    public InMemoryMikroTikRouterStore Routers { get; } = new();

    public RecordingCredentialProtector Credentials { get; } = new();

    public InMemoryActiveBanStore ActiveBanRows { get; } = new();

    public IActiveBanStore ActiveBans { get; }

    public RecordingHttpClientFactory Http { get; } = new();

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 16, 20, 45, 0, TimeSpan.Zero));

    public MikroTikBanActionProvider Provider { get; }

    public async Task<MikroTikRouter> AddRouterAsync(
        string name,
        string password = "router-password",
        bool enabled = true)
    {
        var now = Time.GetUtcNow();
        var router = new MikroTikRouter
        {
            Id = ViegardId.New(),
            Name = name,
            BaseUrl = $"http://{name}.example.com",
            TransportMode = MikroTikRouterTransportMode.PlainHttp,
            PinnedCertificateSha256 = null,
            Username = "viegard",
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = "test",
            RowVersion = 0,
        };
        var created = await Routers.CreateAsync(router, password);
        Assert.True(created.Succeeded);
        return created.Router!;
    }
}

internal sealed class RecordingCredentialProtector : IRouterCredentialProtector
{
    public int UnprotectCalls { get; private set; }

    public string Protect(Guid routerId, string password) => password;

    public string Unprotect(Guid routerId, string ciphertext)
    {
        UnprotectCalls++;
        return ciphertext;
    }
}

internal sealed class AdvancingActiveBanStore(
    IActiveBanStore inner,
    ManualTimeProvider timeProvider,
    TimeSpan advanceAfterUpsert) : IActiveBanStore
{
    public async ValueTask<ActiveBan> UpsertByIpAsync(ActiveBan activeBan, CancellationToken cancellationToken = default)
    {
        var saved = await inner.UpsertByIpAsync(activeBan, cancellationToken).ConfigureAwait(false);
        timeProvider.Advance(advanceAfterUpsert);
        return saved;
    }

    public ValueTask<bool> RemoveByIpAsync(string ip, CancellationToken cancellationToken = default) =>
        inner.RemoveByIpAsync(ip, cancellationToken);

    public ValueTask<IReadOnlyList<ActiveBan>> ListUnexpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        inner.ListUnexpiredAsync(now, cancellationToken);

    public ValueTask<long> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        inner.DeleteExpiredAsync(now, cancellationToken);

    public ValueTask<ActiveBan?> GetByIpAsync(string ip, CancellationToken cancellationToken = default) =>
        inner.GetByIpAsync(ip, cancellationToken);
}

internal sealed class RecordingHttpClientFactory : IMikroTikRouterHttpClientFactory
{
    private readonly Dictionary<Guid, Queue<HttpResponseMessage>> _responses = [];

    public List<RecordedRouterRequest> Requests { get; } = [];

    public Func<CancellationToken, ValueTask>? BeforeRequestAsync { get; set; }

    public void Enqueue(Guid routerId, HttpResponseMessage response)
    {
        if (!_responses.TryGetValue(routerId, out var queue))
        {
            queue = new Queue<HttpResponseMessage>();
            _responses.Add(routerId, queue);
        }

        queue.Enqueue(response);
    }

    public HttpClient CreateClient(MikroTikRouter router, out RouterTransportValidationState transportState)
    {
        transportState = new RouterTransportValidationState();
        return new HttpClient(new RecordingHandler(router.Id, _responses, Requests, BeforeRequestAsync), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private sealed class RecordingHandler(
        Guid routerId,
        Dictionary<Guid, Queue<HttpResponseMessage>> responses,
        List<RecordedRouterRequest> requests,
        Func<CancellationToken, ValueTask>? beforeRequestAsync) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (beforeRequestAsync is not null)
            {
                await beforeRequestAsync(cancellationToken).ConfigureAwait(false);
            }

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            requests.Add(new RecordedRouterRequest(
                routerId,
                request.Method.Method,
                request.RequestUri!.ToString(),
                body,
                request.Headers.Authorization?.Scheme));

            if (responses.TryGetValue(routerId, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }
    }
}

internal sealed record RecordedRouterRequest(
    Guid RouterId,
    string Method,
    string Url,
    string? Body,
    string? AuthorizationScheme);

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}
