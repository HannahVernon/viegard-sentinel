using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Viegard.Application.Configuration;

namespace Viegard.AdminApi.Configuration;

public sealed class RouterConnectivityTester(IRouterCredentialProtector protector)
{
    private const int MaxBodyBytes = 32 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async ValueTask<RouterConnectivityTestResult> TestAsync(
        MikroTikRouter router,
        string passwordCiphertext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        var normalized = MikroTikRouterValidator.NormalizeForSave(router);
        if (string.IsNullOrWhiteSpace(passwordCiphertext))
        {
            return RouterConnectivityTestResult.Failure("Router credential is not set.");
        }

        if (normalized.TransportMode == MikroTikRouterTransportMode.HttpsPinned
            && string.IsNullOrWhiteSpace(normalized.PinnedCertificateSha256))
        {
            return RouterConnectivityTestResult.Failure("Router certificate pin is not configured.");
        }

        using var handler = RouterTransportHandlerFactory.CreateHandler(normalized, out var transportState);

        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout,
        };

        byte[] authBytes = [];
        try
        {
            var password = protector.Unprotect(normalized.Id, passwordCiphertext);
            authBytes = Encoding.UTF8.GetBytes($"{normalized.Username}:{password}");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"{normalized.BaseUrl.TrimEnd('/')}/rest/ip/firewall/address-list"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            // Outbound URL note: the operator is the trusted admin and every
            // probe endpoint is step-up-gated.  The scheme and host-only shape
            // validation in MikroTikRouterValidator is the SSRF boundary for
            // these router probes.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var reason = OneLine(response.ReasonPhrase, 80);
                return RouterConnectivityTestResult.Failure(
                    $"Router returned HTTP {(int)response.StatusCode} {reason}".TrimEnd() + ".");
            }

            byte[] body;
            try
            {
                body = await ReadBodyPrefixAsync(response.Content, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return RouterConnectivityTestResult.Failure(ex.Message);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return RouterConnectivityTestResult.Failure("Router returned HTTP 200 but the response was not a JSON array.");
                }

                var count = document.RootElement.EnumerateArray().Count();
                return RouterConnectivityTestResult.Success(count);
            }
            catch (JsonException)
            {
                return RouterConnectivityTestResult.Failure("Router returned HTTP 200 but the response was not valid JSON.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RouterConnectivityTestResult.Failure("Router test timed out after 10 seconds.");
        }
        catch (HttpRequestException ex) when (transportState.CertificatePinMismatch || ContainsAuthenticationException(ex))
        {
            return RouterConnectivityTestResult.Failure(
                transportState.CertificatePinMismatch ? "Router TLS certificate pin mismatch." : "Router TLS connection failed.");
        }
        catch (HttpRequestException)
        {
            return RouterConnectivityTestResult.Failure("Router connection failed.");
        }
        catch (RouterCredentialProtectionException)
        {
            return RouterConnectivityTestResult.Failure("Router credential could not be decrypted.");
        }
        finally
        {
            Array.Clear(authBytes);
        }
    }

    private static async ValueTask<byte[]> ReadBodyPrefixAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes + 1];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset > maxBytes)
        {
            throw new InvalidOperationException("Router response exceeded the 32768 byte probe limit.");
        }

        return buffer[..offset];
    }

    private static bool ContainsAuthenticationException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static string OneLine(string? value, int maxChars)
    {
        var sanitized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maxChars ? sanitized : sanitized[..maxChars];
    }
}

public sealed record RouterConnectivityTestResult(bool Succeeded, string Message, int? EntryCount)
{
    public static RouterConnectivityTestResult Success(int entryCount) =>
        new(true, $"Router test succeeded: REST address list returned {entryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} entries.", entryCount);

    public static RouterConnectivityTestResult Failure(string message) =>
        new(false, message, null);
}
