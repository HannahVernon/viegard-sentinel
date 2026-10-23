using System.Security.Cryptography;
using System.Text;
using Viegard.Application.Configuration;
using Viegard.Application.Secrets;

namespace Viegard.Application.Auth;

public interface ISecondFactorSecretProtector
{
    bool IsProtected(string stored);
    string Protect(Guid userId, string secretBase32);
    string Unprotect(Guid userId, string stored);
}

public sealed class AesGcmSecondFactorSecretProtector(ISecretProvider secretProvider) : ISecondFactorSecretProtector
{
    private const string Prefix = "enc1:";
    private const int NonceSize = 12, TagSize = 16;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("viegard-totp-secret-v1");
    private readonly object _sync = new();
    private byte[]? _key;

    public bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(Guid userId, string secretBase32)
    {
        var pt = Encoding.UTF8.GetBytes(secretBase32);
        var payload = new byte[NonceSize + pt.Length + TagSize];
        RandomNumberGenerator.Fill(payload.AsSpan(0, NonceSize));
        using var aes = new AesGcm(GetKey(), TagSize);
        aes.Encrypt(payload.AsSpan(0, NonceSize), pt, payload.AsSpan(NonceSize, pt.Length),
            payload.AsSpan(NonceSize + pt.Length, TagSize), Aad(userId));
        CryptographicOperations.ZeroMemory(pt);
        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(Guid userId, string stored)
    {
        if (!IsProtected(stored)) return stored;
        var p = Convert.FromBase64String(stored[Prefix.Length..]);
        if (p.Length <= NonceSize + TagSize) throw new CryptographicException("Truncated secret.");
        var len = p.Length - NonceSize - TagSize;
        var pt = new byte[len];
        try
        {
            using var aes = new AesGcm(GetKey(), TagSize);
            aes.Decrypt(p.AsSpan(0, NonceSize), p.AsSpan(NonceSize, len),
                p.AsSpan(NonceSize + len, TagSize), pt, Aad(userId));
            return Encoding.UTF8.GetString(pt);
        }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }

    private static byte[] Aad(Guid id) =>
        Encoding.UTF8.GetBytes("viegard-totp-user:" + id.ToString("N"));

    private byte[] GetKey()
    {
        lock (_sync)
        {
            if (_key is not null) return _key;
            var master = AesGcmRouterCredentialProtector.LoadKey(secretProvider);
            try { return _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, null, Info); }
            finally { CryptographicOperations.ZeroMemory(master); }
        }
    }
}