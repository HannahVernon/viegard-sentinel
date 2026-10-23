using System.Security.Cryptography;
using System.Text;
using Viegard.Application.Secrets;

namespace Viegard.Application.Configuration;

public interface IRouterCredentialProtector
{
    /// <summary>
    /// Encrypts the password bound to the router id, normalized base URL, username, and transport mode.
    /// </summary>
    string Protect(MikroTikRouter router, string password);

    /// <summary>
    /// Decrypts a stored password.  Fails closed when any bound field differs from the value used at protect time.
    /// </summary>
    string Unprotect(MikroTikRouter router, string ciphertext);

    /// <summary>
    /// True for the legacy format whose authentication binds the router id only; such values should be re-protected.
    /// </summary>
    bool IsLegacyFormat(string ciphertext);
}

public sealed class AesGcmRouterCredentialProtector(ISecretProvider secretProvider) : IRouterCredentialProtector
{
    public const string SecretName = "viegard-router-credentials-key";

    private const byte LegacyVersion = 0x01;
    private const byte Version = 0x02;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize;
    private readonly object _sync = new();
    private byte[]? _key;

    public string Protect(MikroTikRouter router, string password)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (router.Id == Guid.Empty)
        {
            throw new ArgumentException("Router id is required.", nameof(router));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Router password is required.", nameof(password));
        }

        return Seal(Version, BoundAad(router), password);
    }

    public bool IsLegacyFormat(string ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return false;
        }

        try
        {
            var payload = Convert.FromBase64String(ciphertext);
            return payload.Length > 0 && payload[0] == LegacyVersion;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string Seal(byte version, byte[] aad, string password)
    {
        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(password);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

            var payload = new byte[HeaderSize + ciphertext.Length + TagSize];
            payload[0] = version;
            Buffer.BlockCopy(nonce, 0, payload, 1, NonceSize);
            Buffer.BlockCopy(ciphertext, 0, payload, HeaderSize, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, payload, HeaderSize + ciphertext.Length, TagSize);
            return Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(MikroTikRouter router, string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (router.Id == Guid.Empty)
        {
            throw new ArgumentException("Router id is required.", nameof(router));
        }

        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            throw new RouterCredentialProtectionException("Router credential ciphertext is required.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(ciphertext);
        }
        catch (FormatException ex)
        {
            throw new RouterCredentialProtectionException("Router credential ciphertext is not valid base64.", ex);
        }

        if (payload.Length < HeaderSize + TagSize)
        {
            throw new RouterCredentialProtectionException("Router credential ciphertext is truncated.");
        }

        byte[] aad;
        if (payload[0] == Version)
        {
            aad = BoundAad(router);
        }
        else if (payload[0] == LegacyVersion)
        {
            aad = LegacyAad(router.Id);
        }
        else
        {
            throw new RouterCredentialProtectionException("Router credential ciphertext version is not supported.");
        }

        var ciphertextLength = payload.Length - HeaderSize - TagSize;
        var nonce = payload.AsSpan(1, NonceSize);
        var encrypted = payload.AsSpan(HeaderSize, ciphertextLength);
        var tag = payload.AsSpan(HeaderSize + ciphertextLength, TagSize);
        var plaintext = new byte[ciphertextLength];

        try
        {
            using var aes = new AesGcm(GetKey(), TagSize);
            aes.Decrypt(nonce, encrypted, tag, plaintext, aad);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new RouterCredentialProtectionException(
                "Router credential could not be authenticated for this router.  The stored credential does not match the router's current id, base URL, username, and transport mode, or the key differs.  Re-enter the router password to repair it.",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] GetKey()
    {
        if (_key is { } cached)
        {
            return cached;
        }

        lock (_sync)
        {
            return _key ??= LoadKey(secretProvider);
        }
    }

    /// <summary>
    /// Loads and validates the shared 32-byte master key.  Other at-rest protectors derive purpose-specific keys from it.
    /// </summary>
    public static byte[] LoadKey(ISecretProvider secretProvider)
    {
        ArgumentNullException.ThrowIfNull(secretProvider);
        var secret = secretProvider.GetAsync(SecretName).GetAwaiter().GetResult()
            ?? throw KeyFormatException();
        var value = secret.Reveal().Trim();
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw KeyFormatException(ex);
        }

        if (decoded.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw KeyFormatException();
        }

        return decoded;
    }

    private static byte[] LegacyAad(Guid routerId) =>
        Encoding.UTF8.GetBytes(routerId.ToString("N").ToLowerInvariant());

    private static byte[] BoundAad(MikroTikRouter router)
    {
        if (!Enum.IsDefined(router.TransportMode)
            || !MikroTikRouterValidator.TryNormalizeBaseUrl(router.BaseUrl, router.TransportMode, out var baseUrl, out _)
            || !MikroTikRouterValidator.TryNormalizeUsername(router.Username, out var username, out _))
        {
            throw new RouterCredentialProtectionException(
                "Router base URL, username, or transport mode is not valid, so the credential cannot be bound to it.");
        }

        using var stream = new MemoryStream();
        Append(stream, "viegard-router-credential-v2");
        Append(stream, router.Id.ToString("N").ToLowerInvariant());
        Append(stream, baseUrl);
        Append(stream, username);
        Append(stream, router.TransportMode.ToString());
        return stream.ToArray();
    }

    private static void Append(MemoryStream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
    private static RouterCredentialProtectionException KeyFormatException(Exception? inner = null)
    {
        const string message =
            $"Secret '{SecretName}' must contain base64 text for exactly 32 random bytes.  Generate it with: openssl rand -base64 32.";
        return inner is null
            ? new RouterCredentialProtectionException(message)
            : new RouterCredentialProtectionException(message, inner);
    }
}

public sealed class RouterCredentialProtectionException : Exception
{
    public RouterCredentialProtectionException(string message)
        : base(message)
    {
    }

    public RouterCredentialProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
