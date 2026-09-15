namespace Viegard.Application.Logging;

/// <summary>
/// Strips control characters from attacker-influenced strings before they
/// reach log messages, so plain-text log sinks cannot be fed forged lines
/// (CR/LF injection) or terminal escapes (security-audit finding,
/// 2026-08-25).  Structured fields keep their data fidelity; only the
/// human-readable rendering is affected.
/// </summary>
public static class LogSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsControl(c) ? ' ' : c;
            }
        });
    }
}
