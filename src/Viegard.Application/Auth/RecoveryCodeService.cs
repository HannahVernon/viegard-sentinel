using System.Security.Cryptography;
using System.Text;
using Viegard.Domain;
using Viegard.Domain.Admin;

namespace Viegard.Application.Auth;

public sealed class RecoveryCodeService
{
    public IReadOnlyList<string> GenerateCodes(int count = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var codes = new List<string>(count);
        var bytes = new byte[10];
        for (var i = 0; i < count; i++)
        {
            RandomNumberGenerator.Fill(bytes);
            var encoded = Base32Encoding.Encode(bytes).ToLowerInvariant();
            codes.Add($"{encoded[..5]}-{encoded[5..10]}-{encoded[10..15]}-{encoded[15..]}");
        }

        return codes;
    }

    public IReadOnlyList<AdminRecoveryCode> ToRows(Guid userId, IReadOnlyList<string> codes, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return codes.Select(code => new AdminRecoveryCode
        {
            Id = ViegardId.New(),
            UserId = userId,
            CodeHash = Hash(code),
            UsedAt = null,
            CreatedAt = createdAt.ToUniversalTime(),
        }).ToList();
    }

    public static string Hash(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = Normalize(code);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    public static bool Verify(string code, string hash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(code)),
            Encoding.ASCII.GetBytes(hash));

    public static string Normalize(string code) =>
        code.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}
