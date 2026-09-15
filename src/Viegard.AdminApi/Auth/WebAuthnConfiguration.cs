using Microsoft.Extensions.Options;
using Viegard.AdminApi.Configuration;

namespace Viegard.AdminApi.Auth;

public sealed class WebAuthnConfigurationException(string message) : InvalidOperationException(message);

public sealed record WebAuthnConfiguration(string RelyingPartyId, IReadOnlySet<string> Origins)
{
    public static WebAuthnConfiguration Resolve(
        AdminWebAuthnOptions options,
        AdminExposureOptions exposureOptions)
    {
        var loopback = string.Equals(exposureOptions.Exposure, AdminExposureModes.Loopback, StringComparison.OrdinalIgnoreCase);
        var relyingPartyId = options.RelyingPartyId?.Trim();
        var origins = options.Origins
            .Select(o => TryNormalizeOrigin(o, out var normalized) ? normalized : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (loopback)
        {
            relyingPartyId = string.IsNullOrWhiteSpace(relyingPartyId) ? "localhost" : relyingPartyId;
            if (origins.Count == 0)
            {
                origins.Add("http://localhost:8080");
            }
        }

        if (string.IsNullOrWhiteSpace(relyingPartyId) || origins.Count == 0)
        {
            throw new WebAuthnConfigurationException(
                "WebAuthn requires Viegard:Admin:WebAuthn:RelyingPartyId and Origins when admin exposure is not loopback.");
        }

        if (!loopback && origins.Any(origin => new Uri(origin).Scheme != Uri.UriSchemeHttps))
        {
            throw new WebAuthnConfigurationException(
                "WebAuthn origins must use HTTPS when admin exposure is not loopback.");
        }

        return new WebAuthnConfiguration(relyingPartyId, origins);
    }

    public static bool TryNormalizeOrigin(string? value, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.PathAndQuery.Trim('/'))
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        origin = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        return true;
    }
}

public sealed class WebAuthnConfigurationProvider(
    IOptions<AdminWebAuthnOptions> webAuthnOptions,
    IOptions<AdminExposureOptions> exposureOptions)
{
    public WebAuthnConfiguration Get() =>
        WebAuthnConfiguration.Resolve(webAuthnOptions.Value, exposureOptions.Value);
}
