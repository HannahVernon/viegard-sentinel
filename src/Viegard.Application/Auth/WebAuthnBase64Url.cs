namespace Viegard.Application.Auth;

public static class WebAuthnBase64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);

    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim()
            .Replace("-", "+", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal);
        var padding = normalized.Length % 4;
        if (padding == 1)
        {
            return false;
        }

        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + 4 - padding, '=');
        }

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class WebAuthnSignCountPolicy
{
    public static bool IsAcceptable(long storedSignCount, long newSignCount) =>
        newSignCount >= 0 && (storedSignCount <= 0 || newSignCount >= storedSignCount);
}
