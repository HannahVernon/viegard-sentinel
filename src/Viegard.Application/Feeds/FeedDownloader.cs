using System.Net;
using System.Text;

namespace Viegard.Application.Feeds;

public sealed record FeedDownloadResult(string? Payload, string? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Downloads untrusted feed text with an https-only policy, a hard response-size
/// limit, an overall timeout, and manual redirect handling that refuses any hop
/// that is not https.  Pass a client built with automatic redirects disabled.
/// </summary>
public static class FeedDownloader
{
    public static async Task<FeedDownloadResult> DownloadAsync(
        HttpClient client,
        string url,
        FeedGuardOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.FetchTimeoutSeconds));

        try
        {
            var current = url;
            for (var hop = 0; hop <= options.FetchMaxRedirects; hop++)
            {
                if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    return new FeedDownloadResult(null, "Feed URL must be an absolute https URL; non-https requests and redirects are refused.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return new FeedDownloadResult(null, $"HTTP {(int)response.StatusCode} redirect had no Location header.");
                    }

                    current = (location.IsAbsoluteUri ? location : new Uri(uri, location)).AbsoluteUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new FeedDownloadResult(null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
                }

                return await ReadBoundedAsync(response, options.FetchMaxResponseBytes, timeout.Token).ConfigureAwait(false);
            }

            return new FeedDownloadResult(null, $"Feed exceeded {options.FetchMaxRedirects} redirects.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FeedDownloadResult(null, "Feed request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new FeedDownloadResult(null, FeedAddressGuard.Sanitize(ex.Message));
        }
    }

    private static async Task<FeedDownloadResult> ReadBoundedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return new FeedDownloadResult(null, $"Feed response exceeded the {maxBytes}-byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[16 * 1024];
        using var collected = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (collected.Length + read > maxBytes)
            {
                return new FeedDownloadResult(null, $"Feed response exceeded the {maxBytes}-byte limit.");
            }

            collected.Write(buffer, 0, read);
        }

        return new FeedDownloadResult(Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length), null);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
