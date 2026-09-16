using System.Security.Cryptography;
using System.Text;
using Viegard.Application.Secrets;

namespace Viegard.Application.Configuration;

public interface IRouterCredentialProtector
{
    string Protect(Guid routerId, string password);

    string Unprotect(Guid routerId, string ciphertext);
}

public sealed class AesGcmRouterCredentialProtector(ISecretProvider secretProvider) : IRouterCredentialProtector
{
    public const string SecretName = "viegard-router-credentials-key";

    private const byte Version = 0x01;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize;
    private readonly object _sync = new();
    private byte[]? _key;

    public string Protect(Guid routerId, string password)
    {
        if (routerId == Guid.Empty)
        {
            throw new ArgumentException("Router id is required.", nameof(routerId));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Router password is required.", nameof(password));
        }

        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(password);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var aad = Aad(routerId);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

            var payload = new byte[HeaderSize + ciphertext.Length + TagSize];
            payload[0] = Version;
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

    public string Unprotect(Guid routerId, string ciphertext)
    {
        if (routerId == Guid.Empty)
        {
            throw new ArgumentException("Router id is required.", nameof(routerId));
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

        if (payload[0] != Version)
        {
            throw new RouterCredentialProtectionException("Router credential ciphertext version is not supported.");
        }

        var ciphertextLength = payload.Length - HeaderSize - TagSize;
        var nonce = payload.AsSpan(1, NonceSize);
        var encrypted = payload.AsSpan(HeaderSize, ciphertextLength);
        var tag = payload.AsSpan(HeaderSize + ciphertextLength, TagSize);
        var plaintext = new byte[ciphertextLength];
        var aad = Aad(routerId);

        try
        {
            using var aes = new AesGcm(GetKey(), TagSize);
            aes.Decrypt(nonce, encrypted, tag, plaintext, aad);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new RouterCredentialProtectionException(
                "Router credential ciphertext could not be authenticated for this router.",
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
            if (_key is { } lockedCached)
            {
                return lockedCached;
            }

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

            _key = decoded;
            return decoded;
        }
    }

    private static byte[] Aad(Guid routerId) =>
        Encoding.UTF8.GetBytes(routerId.ToString("N").ToLowerInvariant());

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
