using System.Diagnostics.CodeAnalysis;
using Viegard.Domain;

namespace Viegard.Application.Configuration;

public enum MikroTikRouterTransportMode
{
    PlainHttp,
    HttpsTrustAny,
    HttpsPinned,
}

public sealed record MikroTikRouter
{
    public const int MaxNameLength = 64;
    public const int MaxUsernameLength = 64;
    public const int MaxUpdatedByLength = 128;

    public Guid Id { get; init; } = ViegardId.New();

    public required string Name { get; init; }

    public required string BaseUrl { get; init; }

    public required MikroTikRouterTransportMode TransportMode { get; init; }

    public string? PinnedCertificateSha256 { get; init; }

    public required string Username { get; init; }

    public required bool Enabled { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required string UpdatedBy { get; init; }

    public required int RowVersion { get; init; }
}

public static class MikroTikRouterValidator
{
    public const string NameValidationError =
        "Router name must be 1-64 characters using only lowercase letters, digits, and hyphens.";

    public const string BaseUrlShapeError =
        "Router base URL must be an absolute http or https URL containing only a host and optional port.";

    public const string PlainHttpSchemeError =
        "Plain HTTP transport requires an http:// router base URL.";

    public const string HttpsSchemeError =
        "HTTPS router transport requires an https:// router base URL.";

    public const string PinnedCertificateRequiredError =
        "HTTPS pinned transport requires a 64-character lowercase hexadecimal SHA-256 certificate fingerprint.";

    public const string PinnedCertificateBlankError =
        "Pinned certificate SHA-256 must be blank unless the router transport is HTTPS pinned.";

    public const string UsernameValidationError =
        "Router username must be 1-64 printable characters with no control characters.";

    public const string TransportModeValidationError =
        "Router transport mode is not supported.";

    public static bool TryValidate(MikroTikRouter router, out string error)
    {
        ArgumentNullException.ThrowIfNull(router);

        if (router.Id == Guid.Empty)
        {
            error = "Router id is required.";
            return false;
        }

        if (!Enum.IsDefined(router.TransportMode))
        {
            error = TransportModeValidationError;
            return false;
        }

        if (!TryNormalizeName(router.Name, out _, out error)
            || !TryNormalizeBaseUrl(router.BaseUrl, router.TransportMode, out _, out error)
            || !TryNormalizePinnedCertificateSha256(router.PinnedCertificateSha256, router.TransportMode, out _, out error)
            || !TryNormalizeUsername(router.Username, out _, out error))
        {
            return false;
        }

        if (router.CreatedAt == default || router.UpdatedAt == default)
        {
            error = "Router timestamps are required.";
            return false;
        }

        if (router.RowVersion < 0)
        {
            error = "Router row version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static MikroTikRouter NormalizeForSave(MikroTikRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);

        if (!Enum.IsDefined(router.TransportMode))
        {
            throw new InvalidOperationException(TransportModeValidationError);
        }

        if (!TryNormalizeName(router.Name, out var name, out var error)
            || !TryNormalizeBaseUrl(router.BaseUrl, router.TransportMode, out var baseUrl, out error)
            || !TryNormalizePinnedCertificateSha256(router.PinnedCertificateSha256, router.TransportMode, out var pinned, out error)
            || !TryNormalizeUsername(router.Username, out var username, out error))
        {
            throw new InvalidOperationException(error);
        }

        var normalized = router with
        {
            Name = name,
            BaseUrl = baseUrl,
            PinnedCertificateSha256 = pinned,
            Username = username,
            CreatedAt = router.CreatedAt.ToUniversalTime(),
            UpdatedAt = router.UpdatedAt.ToUniversalTime(),
            UpdatedBy = NormalizeUpdatedBy(router.UpdatedBy),
        };

        if (!TryValidate(normalized, out error))
        {
            throw new InvalidOperationException(error);
        }

        return normalized;
    }

    public static bool TryNormalizeName(
        string? candidate,
        [NotNullWhen(true)] out string? normalized,
        out string error)
    {
        normalized = null;
        error = string.Empty;

        var value = candidate?.Trim() ?? string.Empty;
        if (value.Length is 0 or > MikroTikRouter.MaxNameLength)
        {
            error = NameValidationError;
            return false;
        }

        foreach (var character in value)
        {
            var valid = character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character == '-';
            if (!valid)
            {
                error = NameValidationError;
                return false;
            }
        }

        normalized = value;
        return true;
    }

    public static bool TryParseTransportMode(
        string? candidate,
        out MikroTikRouterTransportMode transportMode,
        out string error)
    {
        transportMode = default;
        error = string.Empty;

        if (!Enum.TryParse(candidate, ignoreCase: false, out transportMode)
            || !Enum.IsDefined(transportMode))
        {
            error = TransportModeValidationError;
            return false;
        }

        return true;
    }

    public static bool TryNormalizeBaseUrl(
        string? candidate,
        MikroTikRouterTransportMode transportMode,
        [NotNullWhen(true)] out string? normalized,
        out string error)
    {
        normalized = null;
        error = string.Empty;

        var value = candidate?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = BaseUrlShapeError;
            return false;
        }

        if (transportMode == MikroTikRouterTransportMode.PlainHttp && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = PlainHttpSchemeError;
            return false;
        }

        if ((transportMode is MikroTikRouterTransportMode.HttpsPinned or MikroTikRouterTransportMode.HttpsTrustAny)
            && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = HttpsSchemeError;
            return false;
        }

        normalized = new UriBuilder(uri.Scheme.ToLowerInvariant(), uri.Host.ToLowerInvariant(), uri.IsDefaultPort ? -1 : uri.Port)
            .Uri
            .GetLeftPart(UriPartial.Authority);
        return true;
    }

    public static bool TryNormalizePinnedCertificateSha256(
        string? candidate,
        MikroTikRouterTransportMode transportMode,
        out string? normalized,
        out string error)
    {
        normalized = null;
        error = string.Empty;

        var value = candidate?.Trim() ?? string.Empty;
        if (transportMode != MikroTikRouterTransportMode.HttpsPinned)
        {
            if (value.Length > 0)
            {
                error = PinnedCertificateBlankError;
                return false;
            }

            return true;
        }

        if (!IsLowerHexSha256(value))
        {
            error = PinnedCertificateRequiredError;
            return false;
        }

        normalized = value;
        return true;
    }

    public static bool TryNormalizeUsername(
        string? candidate,
        [NotNullWhen(true)] out string? normalized,
        out string error)
    {
        normalized = null;
        error = string.Empty;

        var value = candidate?.Trim() ?? string.Empty;
        if (value.Length is 0 or > MikroTikRouter.MaxUsernameLength)
        {
            error = UsernameValidationError;
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                error = UsernameValidationError;
                return false;
            }
        }

        normalized = value;
        return true;
    }

    public static string NormalizeUpdatedBy(string? updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= MikroTikRouter.MaxUpdatedByLength
            ? normalized
            : normalized[..MikroTikRouter.MaxUpdatedByLength];
    }

    public static bool IsLowerHexSha256(string? value)
    {
        if (value?.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var valid = character is >= '0' and <= '9'
                || character is >= 'a' and <= 'f';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    public static string FingerprintDisplay(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        return string.Join(
            ":",
            Enumerable.Range(0, fingerprint.Length / 2)
                .Select(index => fingerprint.Substring(index * 2, 2)));
    }
}
