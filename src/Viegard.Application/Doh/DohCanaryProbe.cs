using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Viegard.Application.Doh;

/// <summary>
/// Sends a canary DoH query to a candidate resolver and classifies the response.
/// The probe confirms a reachable, RFC 8484-compliant resolver that returns the
/// operator-published canary token; it is confirmation-only and never the sole
/// basis for removing an address from the curated set.
/// </summary>
public static class DohCanaryProbe
{
    public const string DnsMessageMediaType = "application/dns-message";
    private const ushort DnsTypeTxt = 16;
    private const ushort DnsClassIn = 1;

    /// <summary>
    /// Probes <paramref name="address"/> over HTTPS at the configured DoH path.
    /// Certificate validation is delegated to the supplied <paramref name="client"/>
    /// (probing by IP means the presented certificate will not match the IP, so the
    /// probe client is configured to tolerate that; the canary token is public).
    /// </summary>
    /// <remarks>
    /// A resolver probed by IP often rejects the default POST-over-HTTP/2 request
    /// even though it is a compliant DoH server: observed failures include HTTP 505
    /// (the server does not accept our HTTP version), 400/404/405 (it wants GET or a
    /// different framing), and dropped connections.  To lift the confirmation rate the
    /// probe walks a small fallback ladder - POST then GET, each over HTTP/2 then
    /// HTTP/1.1 - and returns <see cref="DohProbeStatus.Confirmed"/> as soon as any
    /// variant returns the canary token.  When none confirm, the most informative
    /// non-confirming outcome is reported.  The ladder short-circuits once an address
    /// stops answering, so an unreachable host does not pay the full ladder cost.
    /// </remarks>
    public static async Task<DohProbeOutcome> ProbeAsync(
        HttpClient client,
        string address,
        string endpointPath,
        string canaryFqdn,
        string expectedToken,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!TryBuildTxtQuery(canaryFqdn, out var query, out _))
        {
            return new DohProbeOutcome(DohProbeStatus.RespondedNonCompliant, null, false);
        }

        var path = string.IsNullOrWhiteSpace(endpointPath) ? "/dns-query" : endpointPath;
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var postUrl = $"https://{address}{path}";
        var getUrl = $"{postUrl}?dns={Base64UrlEncode(query)}";

        (HttpMethod Method, string Url, Version Version)[] attempts =
        [
            (HttpMethod.Post, postUrl, HttpVersion.Version20),
            (HttpMethod.Post, postUrl, HttpVersion.Version11),
            (HttpMethod.Get, getUrl, HttpVersion.Version20),
            (HttpMethod.Get, getUrl, HttpVersion.Version11),
        ];

        var best = new DohProbeOutcome(DohProbeStatus.Refused, null, false);
        var bestRank = -1;
        var consecutiveNoResponse = 0;

        foreach (var attempt in attempts)
        {
            var outcome = await AttemptAsync(
                client,
                attempt.Method,
                attempt.Url,
                attempt.Version,
                query,
                expectedToken,
                timeout,
                cancellationToken).ConfigureAwait(false);

            if (outcome.Status == DohProbeStatus.Confirmed)
            {
                return outcome;
            }

            var rank = OutcomeRank(outcome);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = outcome;
            }

            // An address that returns no HTTP response twice in a row is not
            // reachable, so the remaining method/version variants cannot help.
            var noResponse = outcome.HttpStatus is null
                && outcome.Status is DohProbeStatus.Timeout or DohProbeStatus.Refused;
            if (noResponse)
            {
                if (++consecutiveNoResponse >= 2)
                {
                    break;
                }
            }
            else
            {
                consecutiveNoResponse = 0;
            }
        }

        return best;
    }

    private static async Task<DohProbeOutcome> AttemptAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Version version,
        byte[] query,
        string expectedToken,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Version = version,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            request.Headers.Accept.ParseAdd(DnsMessageMediaType);

            if (method == HttpMethod.Post)
            {
                request.Content = new ByteArrayContent(query);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(DnsMessageMediaType);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var httpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new DohProbeOutcome(DohProbeStatus.Refused, httpStatus, false);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var body = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            var isDnsMessage = string.Equals(mediaType, DnsMessageMediaType, StringComparison.OrdinalIgnoreCase);
            var tokenMatched = ContainsToken(body, expectedToken);

            return isDnsMessage && tokenMatched
                ? new DohProbeOutcome(DohProbeStatus.Confirmed, httpStatus, true)
                : new DohProbeOutcome(DohProbeStatus.RespondedNonCompliant, httpStatus, tokenMatched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DohProbeOutcome(DohProbeStatus.Timeout, null, false);
        }
        catch (HttpRequestException)
        {
            return new DohProbeOutcome(DohProbeStatus.Refused, null, false);
        }
    }

    // Reports which non-confirming outcome is most informative for the router
    // comment annotation: a 200 that missed compliance is closer to working than
    // an HTTP error, which is in turn more useful than a timeout or a dropped
    // connection that carried no HTTP response.
    private static int OutcomeRank(DohProbeOutcome outcome) => outcome.Status switch
    {
        DohProbeStatus.Confirmed => 5,
        DohProbeStatus.RespondedNonCompliant => 4,
        DohProbeStatus.Refused when outcome.HttpStatus is not null => 3,
        DohProbeStatus.Timeout => 2,
        _ => 1,
    };

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Builds a DNS wire-format query message for the canary TXT record (RFC 1035 / RFC 8484).</summary>
    public static bool TryBuildTxtQuery(string? fqdn, out byte[] query, out string error)
    {
        query = [];
        error = string.Empty;
        if (!DohBlocklistSettingsValidator.TryNormalizeCanaryFqdn(fqdn, out var name))
        {
            error = "Canary FQDN is invalid.";
            return false;
        }

        using var buffer = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header[2] = 0x01; // RD = 1
        header[5] = 0x01; // QDCOUNT = 1
        buffer.Write(header);

        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                error = "Canary FQDN label length is invalid.";
                return false;
            }

            buffer.WriteByte((byte)bytes.Length);
            buffer.Write(bytes);
        }

        buffer.WriteByte(0); // root label
        buffer.WriteByte((byte)(DnsTypeTxt >> 8));
        buffer.WriteByte((byte)(DnsTypeTxt & 0xFF));
        buffer.WriteByte((byte)(DnsClassIn >> 8));
        buffer.WriteByte((byte)(DnsClassIn & 0xFF));

        query = buffer.ToArray();
        return true;
    }

    /// <summary>True when the ASCII token appears verbatim in the response body.</summary>
    public static bool ContainsToken(byte[] body, string expectedToken)
    {
        if (body is null || body.Length == 0 || string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        var needle = Encoding.ASCII.GetBytes(expectedToken);
        if (needle.Length == 0 || needle.Length > body.Length)
        {
            return false;
        }

        for (var start = 0; start <= body.Length - needle.Length; start++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (body[start + offset] != needle[offset])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record DohProbeOutcome(DohProbeStatus Status, int? HttpStatus, bool TokenMatched);
