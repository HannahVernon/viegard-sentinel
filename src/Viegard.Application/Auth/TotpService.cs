using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Viegard.Application.Auth;

public sealed class TotpService(TimeProvider? timeProvider = null)
{
    public const int RuntimeDigits = 6;
    public const int StepSeconds = 30;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string GenerateSecret()
    {
        Span<byte> secret = stackalloc byte[20];
        RandomNumberGenerator.Fill(secret);
        return Base32Encoding.Encode(secret);
    }

    public string BuildOtpAuthUri(string username, string secretBase32, string issuer = "Viegard", int digits = RuntimeDigits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretBase32);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ValidateDigits(digits);

        var label = Uri.EscapeDataString($"{issuer}:{username}");
        var escapedIssuer = Uri.EscapeDataString(issuer);
        var escapedSecret = Uri.EscapeDataString(secretBase32);
        return $"otpauth://totp/{label}?secret={escapedSecret}&issuer={escapedIssuer}&algorithm=SHA1&digits={digits}&period={StepSeconds}";
    }

    public bool VerifyCode(string secretBase32, string code, long? lastAcceptedStep, out long acceptedStep)
    {
        var now = _time.GetUtcNow();
        return VerifyCode(secretBase32, code, lastAcceptedStep, now, RuntimeDigits, out acceptedStep);
    }

    public static bool VerifyCode(
        string secretBase32,
        string code,
        long? lastAcceptedStep,
        DateTimeOffset timestamp,
        int digits,
        out long acceptedStep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretBase32);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ValidateDigits(digits);

        acceptedStep = 0;
        var normalizedCode = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalizedCode.Length != digits || normalizedCode.Any(c => c < '0' || c > '9'))
        {
            return false;
        }

        var secret = Base32Encoding.Decode(secretBase32);
        var currentStep = ToStep(timestamp);
        for (var offset = -1; offset <= 1; offset++)
        {
            var candidateStep = currentStep + offset;
            if (candidateStep < 0 || candidateStep <= lastAcceptedStep)
            {
                continue;
            }

            var candidate = ComputeCode(secret, candidateStep, digits);
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(normalizedCode),
                System.Text.Encoding.ASCII.GetBytes(candidate)))
            {
                acceptedStep = candidateStep;
                return true;
            }
        }

        return false;
    }

    public static string ComputeCode(ReadOnlySpan<byte> secret, DateTimeOffset timestamp, int digits = RuntimeDigits) =>
        ComputeCode(secret, ToStep(timestamp), digits);

    public static string ComputeCode(ReadOnlySpan<byte> secret, long step, int digits = RuntimeDigits)
    {
        ValidateDigits(digits);

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString($"D{digits}", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static long ToStep(DateTimeOffset timestamp) => timestamp.ToUnixTimeSeconds() / StepSeconds;

    private static void ValidateDigits(int digits)
    {
        if (digits is < 6 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), "TOTP digit count must be between 6 and 8.");
        }
    }
}
