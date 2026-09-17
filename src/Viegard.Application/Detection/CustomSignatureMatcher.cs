using Viegard.Domain.Configuration;
using Viegard.Domain.Events;

namespace Viegard.Application.Detection;

public static class CustomSignatureMatcher
{
    public static bool Matches(NormalizedEvent e, CustomSignature signature, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(options);

        var targetValue = GetTargetValue(e, signature.Target);
        if (string.IsNullOrEmpty(targetValue))
        {
            return false;
        }

        var scanned = DetectionText.Limit(targetValue, options.MaxInputCharsToScan);
        return HasAdditionalPatterns(signature)
            ? ContainsAll(scanned, signature)
            : signature.MatchType switch
            {
                CustomSignatureMatchType.Contains => scanned.Contains(signature.Pattern, StringComparison.OrdinalIgnoreCase),
                CustomSignatureMatchType.Prefix => scanned.StartsWith(signature.Pattern, StringComparison.OrdinalIgnoreCase),
                CustomSignatureMatchType.ContainsAll => ContainsAll(scanned, signature),
                _ => false,
            };
    }

    public static string GetTargetValue(NormalizedEvent e, CustomSignatureTarget target) =>
        target == CustomSignatureTarget.EventKind
            ? EventKindName.Of(e.Payload)
            : e.Payload is HttpRequestEvent http ? GetTargetValue(http, target) : string.Empty;

    private static string GetTargetValue(HttpRequestEvent http, CustomSignatureTarget target) => target switch
    {
        CustomSignatureTarget.HttpUri => http.Uri ?? string.Empty,
        CustomSignatureTarget.HttpQuery => QueryFromUri(http.Uri),
        CustomSignatureTarget.HttpUserAgent => http.UserAgent ?? string.Empty,
        CustomSignatureTarget.HttpPath => PathFromUri(http.Uri),
        _ => string.Empty,
    };

    private static bool ContainsAll(string scanned, CustomSignature signature) =>
        scanned.Contains(signature.Pattern, StringComparison.OrdinalIgnoreCase)
        && (signature.AdditionalPatterns ?? [])
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .All(term => scanned.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool HasAdditionalPatterns(CustomSignature signature) =>
        (signature.AdditionalPatterns ?? []).Any(term => !string.IsNullOrWhiteSpace(term));

    private static string QueryFromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return string.Empty;
        }

        var queryStart = uri.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0 || queryStart == uri.Length - 1)
        {
            return string.Empty;
        }

        var fragmentStart = uri.IndexOf('#', queryStart + 1);
        return fragmentStart < 0
            ? uri[(queryStart + 1)..]
            : uri[(queryStart + 1)..fragmentStart];
    }

    private static string PathFromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return string.Empty;
        }

        var queryStart = uri.IndexOf('?', StringComparison.Ordinal);
        var fragmentStart = uri.IndexOf('#', StringComparison.Ordinal);
        var end = queryStart < 0
            ? fragmentStart < 0 ? uri.Length : fragmentStart
            : fragmentStart < 0 ? queryStart : Math.Min(queryStart, fragmentStart);
        return uri[..end];
    }
}
