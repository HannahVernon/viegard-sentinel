using System.Text.RegularExpressions;

namespace Viegard.Sources.Imap;

/// <summary>
/// Extracts http/https links from mail bodies for evidence purposes.  Regex
/// extraction is intentionally simple: it feeds classification evidence, not
/// security-critical parsing, and avoids adding an HTML-parser dependency.
/// Extracted values remain untrusted observed data.
/// </summary>
public static partial class LinkExtractor
{
    /// <summary>Cap to keep hostile mails from bloating events.</summary>
    public const int MaxLinks = 100;

    public static IReadOnlyList<string> Extract(string? textBody, string? htmlBody)
    {
        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string candidate)
        {
            var trimmed = candidate.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
            if (trimmed.Length > 0 && seen.Add(trimmed) && links.Count < MaxLinks)
            {
                links.Add(trimmed);
            }
        }

        if (!string.IsNullOrEmpty(htmlBody))
        {
            foreach (Match match in HrefPattern().Matches(htmlBody))
            {
                Add(match.Groups["url"].Value);
            }
        }

        if (!string.IsNullOrEmpty(textBody))
        {
            foreach (Match match in UrlPattern().Matches(textBody))
            {
                Add(match.Value);
            }
        }

        return links;
    }

    [GeneratedRegex("""href\s*=\s*["'](?<url>https?://[^"'<>\s]+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
