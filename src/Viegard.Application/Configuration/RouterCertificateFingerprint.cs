using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Viegard.Application.Configuration;

public static class RouterCertificateFingerprint
{
    public static string Sha256LowerHex(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
    }
}
