using System.Net.Security;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace Viegard.Application.Configuration;

public sealed class RouterTransportValidationState
{
    public bool CertificatePinMismatch { get; internal set; }
}

public static class RouterTransportHandlerFactory
{
    public static SocketsHttpHandler CreateHandler(
        MikroTikRouter router,
        out RouterTransportValidationState state)
    {
        ArgumentNullException.ThrowIfNull(router);

        state = new RouterTransportValidationState();
        var handler = new SocketsHttpHandler();
        if (router.TransportMode == MikroTikRouterTransportMode.HttpsTrustAny)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (router.TransportMode == MikroTikRouterTransportMode.HttpsPinned)
        {
            var capturedState = state;
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                var matches = CertificatePinMatches(certificate, router.PinnedCertificateSha256);
                capturedState.CertificatePinMismatch = !matches;
                return matches;
            };
        }

        return handler;
    }

    public static bool CertificatePinMatches(X509Certificate? certificate, string? expectedSha256)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        var actual = RouterCertificateFingerprint.Sha256LowerHex(certificate);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
