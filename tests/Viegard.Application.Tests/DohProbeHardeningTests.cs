using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Viegard.Application.Doh;
using Viegard.Application.Policy;

namespace Viegard.Application.Tests;

public sealed class DohProbeHardeningTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("224.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("not-an-ip")]
    [InlineData("1.1.1.0/24")]
    [InlineData("")]
    public void Destination_is_refused_for_non_public_or_malformed_addresses(string address)
    {
        Assert.False(DohProbeDestination.IsAllowed(address, null));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Destination_is_allowed_for_public_addresses(string address)
    {
        Assert.True(DohProbeDestination.IsAllowed(address, null));
    }

    [Fact]
    public void Destination_is_refused_when_address_is_in_protected_list()
    {
        var list = new ProtectedAddressList(["203.0.113.0/24"]);

        Assert.False(DohProbeDestination.IsAllowed("203.0.113.9", list));
        Assert.True(DohProbeDestination.IsAllowed("198.51.100.9", list));
    }

    [Fact]
    public async Task Probe_sends_no_request_to_private_destination()
    {
        var handler = new CountingHandler(_ => Response("TOKEN"));
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "192.168.1.10", "/dns-query", "_doh_canary.example.com", "TOKEN", TimeSpan.FromSeconds(5));

        Assert.Equal(DohProbeStatus.Refused, outcome.Status);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Probe_ignores_token_beyond_response_cap()
    {
        var padding = new byte[DohCanaryProbe.MaxResponseBytes];
        var body = padding.Concat(Encoding.ASCII.GetBytes("TOKEN")).ToArray();
        var handler = new CountingHandler(_ => Response(body));
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "1.2.3.4", "/dns-query", "_doh_canary.example.com", "TOKEN", TimeSpan.FromSeconds(5));

        Assert.NotEqual(DohProbeStatus.Confirmed, outcome.Status);
    }

    [Fact]
    public async Task Probe_does_not_follow_redirect_responses()
    {
        var handler = new CountingHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://10.0.0.1/dns-query");
            return redirect;
        });
        using var client = new HttpClient(handler);

        var outcome = await DohCanaryProbe.ProbeAsync(
            client, "1.2.3.4", "/dns-query", "_doh_canary.example.com", "TOKEN", TimeSpan.FromSeconds(5));

        Assert.NotEqual(DohProbeStatus.Confirmed, outcome.Status);
        Assert.All(handler.Urls, url => Assert.StartsWith("https://1.2.3.4/", url, StringComparison.Ordinal));
    }

    private static HttpResponseMessage Response(string token) => Response(Encoding.ASCII.GetBytes(token));

    private static HttpResponseMessage Response(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(DohCanaryProbe.DnsMessageMediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Count => Urls.Count;

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(responder(request));
        }
    }
}
