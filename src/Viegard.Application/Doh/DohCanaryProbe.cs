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
        var url = $"https://{address}{(path.StartsWith('/') ? path : "/" + path)}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(query);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(DnsMessageMediaType);
            request.Headers.Accept.ParseAdd(DnsMessageMediaType);

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
