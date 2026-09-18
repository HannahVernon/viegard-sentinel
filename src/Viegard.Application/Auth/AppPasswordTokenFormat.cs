using System.Security.Cryptography;
using System.Text;

namespace Viegard.Application.Auth;

/// <summary>
/// Generates and validates the read-only app-password token format:
/// <c>viegard_ro_</c> + 16 lowercase hex lookup characters + 43 base64url
/// secret characters.  The whole token string is hashed with SHA-256 for
/// storage; with 256 bits of secret entropy a fast hash is appropriate (a
/// key-derivation work factor defends low-entropy human passwords, which
/// this is not), and comparisons are constant-time.
/// </summary>
public static class AppPasswordTokenFormat
{
    public const string Prefix = "viegard_ro_";
    public const int LookupKeyLength = 16;
    private const int SecretByteLength = 32;

    public sealed record GeneratedToken(string Token, string LookupKey, string SecretHash);

    public static GeneratedToken Generate()
    {
        var lookupKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(LookupKeyLength / 2));
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretByteLength));
        var token = Prefix + lookupKey + secret;
        return new GeneratedToken(token, lookupKey, HashToken(token));
    }

    public static bool TryParseLookupKey(string? token, out string lookupKey)
    {
        lookupKey = string.Empty;
        if (token is null
            || !token.StartsWith(Prefix, StringComparison.Ordinal)
            || token.Length <= Prefix.Length + LookupKeyLength)
        {
            return false;
        }

        var candidate = token.Substring(Prefix.Length, LookupKeyLength);
        foreach (var character in candidate)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        lookupKey = candidate;
        return true;
    }

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Constant-time comparison of a presented token against a stored hash.</summary>
    public static bool Matches(string presentedToken, string storedSecretHash)
    {
        var presented = Encoding.UTF8.GetBytes(HashToken(presentedToken));
        var stored = Encoding.UTF8.GetBytes(storedSecretHash);
        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
