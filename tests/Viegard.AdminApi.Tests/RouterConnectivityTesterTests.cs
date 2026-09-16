using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Viegard.Application.Configuration;

namespace Viegard.AdminApi.Tests;

public sealed class RouterConnectivityTesterTests
{
    [Fact]
    public void Pinned_fingerprint_comparison_accepts_matching_certificate()
    {
        using var certificate = CreateCertificate();
        var fingerprint = RouterCertificateFingerprint.Sha256LowerHex(certificate);

        Assert.True(RouterTransportHandlerFactory.CertificatePinMatches(certificate, fingerprint));
        Assert.True(RouterTransportHandlerFactory.CertificatePinMatches(certificate, fingerprint.ToUpperInvariant()));
    }

    [Fact]
    public void Pinned_fingerprint_comparison_rejects_missing_or_different_certificate()
    {
        using var certificate = CreateCertificate();
        var different = new string('a', 64);

        Assert.False(RouterTransportHandlerFactory.CertificatePinMatches(null, different));
        Assert.False(RouterTransportHandlerFactory.CertificatePinMatches(certificate, null));
        Assert.False(RouterTransportHandlerFactory.CertificatePinMatches(certificate, different));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=router.example.com",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
