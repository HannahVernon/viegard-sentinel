namespace Viegard.Application.Feeds;

/// <summary>Builds the dedicated HTTP client for third-party feed downloads.</summary>
public static class FeedHttpClient
{
    public const string ServiceKey = "viegard-feeds";

    /// <summary>
    /// Automatic redirects are off so <see cref="FeedDownloader"/> can refuse any
    /// non-https hop; the client timeout is a backstop beyond the per-download timeout.
    /// </summary>
    public static HttpClient Create(FeedGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(options.FetchTimeoutSeconds, 15)),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(options.FetchTimeoutSeconds + 5),
            MaxResponseContentBufferSize = options.FetchMaxResponseBytes,
        };
    }
}
