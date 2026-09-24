using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Viegard.Application.Doh;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class DohBlocklistTests
{
    [Fact]
    public async Task Probe_trigger_wakes_a_waiter_after_a_request()
    {
        using var trigger = new InMemoryDohProbeTrigger();

        await trigger.RequestAsync();

        Assert.True(await trigger.WaitForRequestAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task Probe_trigger_reports_no_request_when_none_was_made()
    {
        using var trigger = new InMemoryDohProbeTrigger();

        Assert.False(await trigger.WaitForRequestAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task Probe_trigger_coalesces_repeated_requests_into_one_wake()
    {
        using var trigger = new InMemoryDohProbeTrigger();

        await trigger.RequestAsync();
        await trigger.RequestAsync();
        await trigger.RequestAsync();

        Assert.True(await trigger.WaitForRequestAsync(TimeSpan.Zero));
        Assert.False(await trigger.WaitForRequestAsync(TimeSpan.Zero));
    }

    [Fact]
    public void Feed_parser_keeps_ipv4_and_cidr_drops_ipv6_and_comments()
    {
        const string payload =
            "# curated DoH IPv4 feed\n" +
            "1.1.1.1\n" +
            "8.8.8.8   # google\n" +
            "192.0.2.0/24\n" +
            ";comment-line\n" +
            "2606:4700:4700::1111\n" +
            "1.1.1.1\n";

        var parsed = DohFeedParser.ParseText(payload);

        Assert.Equal(["1.1.1.1", "8.8.8.8", "192.0.2.0/24"], parsed);
    }

    [Fact]
    public void Feed_parser_returns_empty_for_blank_payload()
    {
        Assert.Empty(DohFeedParser.ParseText("   \n\n"));
    }

    [Fact]
    public void Canary_query_builder_produces_wire_format_txt_query()
    {
        Assert.True(DohCanaryProbe.TryBuildTxtQuery("_doh_canary.example.com", out var query, out _));

        // Header (12) + labels (1+11 + 1+7 + 1+3) + root (1) + qtype (2) + qclass (2).
        Assert.Equal(41, query.Length);
        Assert.Equal(0x01, query[2]); // RD flag
        Assert.Equal(0x01, query[5]); // QDCOUNT low byte
        Assert.Equal(0x00, query[^4]);
        Assert.Equal(0x10, query[^3]); // QTYPE = 16 (TXT)
        Assert.Equal(0x00, query[^2]);
        Assert.Equal(0x01, query[^1]); // QCLASS = 1 (IN)
    }

    [Fact]
    public void Canary_query_builder_rejects_invalid_fqdn()
    {
        Assert.False(DohCanaryProbe.TryBuildTxtQuery("not-a-fqdn", out _, out _));
    }

    [Fact]
    public void Contains_token_matches_ascii_token_in_body()
    {
        var body = Encoding.ASCII.GetBytes("prefix-DEADBEEF-suffix");

        Assert.True(DohCanaryProbe.ContainsToken(body, "DEADBEEF"));
        Assert.False(DohCanaryProbe.ContainsToken(body, "CAFEBABE"));
        Assert.False(DohCanaryProbe.ContainsToken(body, string.Empty));
    }

    [Fact]
    public void Validator_accepts_defaults()
    {
        Assert.True(DohBlocklistSettingsValidator.TryValidate(new DohBlocklistSettings(), out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Validator_rejects_non_absolute_primary_feed_url()
    {
        var settings = new DohBlocklistSettings { PrimaryFeedUrl = "not-a-url" };

        Assert.False(DohBlocklistSettingsValidator.TryValidate(settings, out var error));
        Assert.Equal(DohBlocklistSettingsValidator.PrimaryFeedUrlError, error);
    }

    [Fact]
    public void Validator_rejects_out_of_range_probe_concurrency()
    {
        var settings = new DohBlocklistSettings { ProbeConcurrency = 0 };

        Assert.False(DohBlocklistSettingsValidator.TryValidate(settings, out var error));
        Assert.Equal(DohBlocklistSettingsValidator.ProbeConcurrencyError, error);
    }

    [Fact]
    public async Task Settings_store_seeds_then_enforces_optimistic_concurrency()
    {
        var store = new InMemoryDohBlocklistSettingsStore();
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        var seed = await store.UpdateAsync(new DohBlocklistSettings(), expectedRowVersion: 0, "hannah", now);
        Assert.True(seed.Succeeded);
        Assert.Equal(1, seed.Settings!.RowVersion);

        var update = await store.UpdateAsync(
            new DohBlocklistSettings { ApplyToRouters = true },
            expectedRowVersion: 1,
            "hannah",
            now);
        Assert.True(update.Succeeded);
        Assert.Equal(2, update.Settings!.RowVersion);
        Assert.True(update.Settings.ApplyToRouters);

        var stale = await store.UpdateAsync(new DohBlocklistSettings(), expectedRowVersion: 1, "hannah", now);
        Assert.False(stale.Succeeded);
        Assert.Equal(DohBlocklistSettingsSaveStatus.Conflict, stale.Status);
    }

    [Fact]
    public async Task Probe_confirms_on_first_post_over_http2()
    {
        var handler = new StubProbeHandler(_ => DnsMessageResponse("TOKEN123"));
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "1.2.3.4", "/dns-query", "_doh_canary.example.com", "TOKEN123", TimeSpan.FromSeconds(5));

        Assert.Equal(DohProbeStatus.Confirmed, outcome.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(HttpVersion.Version20, request.Version);
    }

    [Fact]
    public async Task Probe_retries_http11_when_http2_version_not_supported()
    {
        var handler = new StubProbeHandler(request => request.Version == HttpVersion.Version20
            ? new HttpResponseMessage(HttpStatusCode.HttpVersionNotSupported)
            : DnsMessageResponse("TOK"));
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "1.2.3.4", "/dns-query", "_doh_canary.example.com", "TOK", TimeSpan.FromSeconds(5));

        Assert.Equal(DohProbeStatus.Confirmed, outcome.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Equal(HttpVersion.Version11, handler.Requests[1].Version);
    }

    [Fact]
    public async Task Probe_falls_back_to_get_when_post_is_rejected()
    {
        var handler = new StubProbeHandler(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : DnsMessageResponse("TOK"));
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "1.2.3.4", "/dns-query", "_doh_canary.example.com", "TOK", TimeSpan.FromSeconds(5));

        Assert.Equal(DohProbeStatus.Confirmed, outcome.Status);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[2].Method);
        Assert.Contains("?dns=", handler.Requests[2].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_short_circuits_after_two_unanswered_attempts()
    {
        var handler = new StubProbeHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "1.2.3.4", "/dns-query", "_doh_canary.example.com", "TOK", TimeSpan.FromSeconds(5));

        Assert.Equal(DohProbeStatus.Refused, outcome.Status);
        Assert.Null(outcome.HttpStatus);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Probe_reports_non_compliant_when_no_variant_confirms()
    {
        var handler = new StubProbeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not a dns message") });
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "1.2.3.4", "/dns-query", "_doh_canary.example.com", "TOK", TimeSpan.FromSeconds(5));

        Assert.Equal(DohProbeStatus.RespondedNonCompliant, outcome.Status);
        Assert.Equal(4, handler.Requests.Count);
    }

    private static HttpResponseMessage DnsMessageResponse(string token)
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes($"....{token}...."));
        content.Headers.ContentType = new MediaTypeHeaderValue(DohCanaryProbe.DnsMessageMediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string Method, Version Version, string Url)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.Version, request.RequestUri!.ToString()));
            try
            {
                return Task.FromResult(responder(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
