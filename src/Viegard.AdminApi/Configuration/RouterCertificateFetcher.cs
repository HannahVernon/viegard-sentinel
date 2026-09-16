using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Viegard.Application.Configuration;

namespace Viegard.AdminApi.Configuration;

public sealed class RouterCertificateFetcher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async ValueTask<RouterCertificateInfo> FetchAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new RouterProbeException("Certificate fetch requires an HTTPS router base URL.");
        }

        X509Certificate2? captured = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
            await using var ssl = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                {
                    // Fetch-only callback: the operator needs to inspect the
                    // presented certificate before deciding whether to pin it.
                    // Returning true here does not establish trust or persist
                    // anything; the save action is a separate step-up-gated
                    // mutation.
                    if (certificate is not null)
                    {
                        captured = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
                    }

                    return true;
                });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.Host,
                EnabledSslProtocols = SslProtocols.None,
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RouterProbeException("Certificate fetch timed out after 10 seconds.", ex);
        }
        catch (AuthenticationException ex)
        {
            throw new RouterProbeException("TLS certificate fetch failed.", ex);
        }
        catch (SocketException ex)
        {
            throw new RouterProbeException("Could not connect to the router HTTPS endpoint.", ex);
        }
        catch (IOException ex)
        {
            throw new RouterProbeException("Could not read the router TLS certificate.", ex);
        }

        if (captured is null)
        {
            throw new RouterProbeException("The router did not present a certificate.");
        }

        using (captured)
        {
            var fingerprint = RouterCertificateFingerprint.Sha256LowerHex(captured);
            return new RouterCertificateInfo(
                captured.Subject,
                captured.Issuer,
                captured.NotBefore.ToUniversalTime(),
                captured.NotAfter.ToUniversalTime(),
                fingerprint);
        }
    }
}

public sealed record RouterCertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Sha256Fingerprint)
{
    public string FingerprintDisplay =>
        MikroTikRouterValidator.FingerprintDisplay(Sha256Fingerprint);
}

public sealed class RouterProbeException : Exception
{
    public RouterProbeException(string message)
        : base(message)
    {
    }

    public RouterProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
