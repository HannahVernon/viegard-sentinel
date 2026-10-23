using System.Net;
using System.Text;
using Viegard.Application.Doh;
using Viegard.Application.Feeds;

namespace Viegard.Application.Tests;

public sealed class FeedDownloaderTests
{
    private static readonly FeedGuardOptions Options = new() { FetchMaxResponseBytes = 1024 };

    [Fact]
    public async Task Refuses_non_https_urls_without_sending_a_request()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await FeedDownloader.DownloadAsync(new HttpClient(handler), "http://feeds.example/list.txt", Options, default);

        Assert.False(result.Succeeded);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Follows_https_redirects_and_refuses_downgrade_to_http()
    {
        var https = new SequenceHandler(request => request.RequestUri!.AbsolutePath == "/start"
            ? Redirect("https://mirror.example/list.txt")
            : Text("203.0.113.1\n"));
        var accepted = await FeedDownloader.DownloadAsync(new HttpClient(https), "https://feeds.example/start", Options, default);
        Assert.Equal("203.0.113.1\n", accepted.Payload);

        var downgrade = new SequenceHandler(_ => Redirect("http://mirror.example/list.txt"));
        var refused = await FeedDownloader.DownloadAsync(new HttpClient(downgrade), "https://feeds.example/start", Options, default);
        Assert.False(refused.Succeeded);
        Assert.Equal(1, downgrade.Calls);
    }

    [Fact]
    public async Task Stops_after_the_configured_redirect_count()
    {
        var handler = new SequenceHandler(_ => Redirect("https://feeds.example/loop"));

        var result = await FeedDownloader.DownloadAsync(
            new HttpClient(handler),
            "https://feeds.example/loop",
            new FeedGuardOptions { FetchMaxRedirects = 2 },
            default);

        Assert.False(result.Succeeded);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Rejects_responses_over_the_size_limit_even_without_content_length()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('1', 4096)))),
        });

        var result = await FeedDownloader.DownloadAsync(new HttpClient(handler), "https://feeds.example/big", Options, default);

        Assert.False(result.Succeeded);
        Assert.Contains("limit", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_timeout_without_throwing()
    {
        var handler = new HangingHandler();

        var result = await FeedDownloader.DownloadAsync(
            new HttpClient(handler),
            "https://feeds.example/slow",
            new FeedGuardOptions { FetchTimeoutSeconds = 1 },
            default);

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://feeds.example/list.txt", false)]
    [InlineData("https://feeds.example/list.txt", true)]
    public void Settings_validators_require_https(string url, bool valid)
    {
        Assert.Equal(valid, DohBlocklistSettingsValidator.TryNormalizeFeedUrl(url, required: true, out _, out _));
        Assert.Equal(
            valid,
            Viegard.Application.Configuration.JetPackFeedSettingsValidator.TryNormalizeFeedUrl(url, out _, out _));
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class SequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
