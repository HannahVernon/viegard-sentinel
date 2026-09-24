namespace Viegard.Application.Doh;

/// <summary>
/// Parses curated DoH IP feeds.  The dibdot and jpgpi250 lists are plain text,
/// one address per line, with optional <c>#</c> comments.  Only IPv4 addresses
/// and IPv4 CIDR ranges are retained (the router firewall list is IPv4).
/// </summary>
public static class DohFeedParser
{
    /// <summary>Extracts and normalizes IPv4 addresses/CIDRs from a plain-text feed body.</summary>
    public static IReadOnlyList<string> ParseText(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in payload.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var hashIndex = line.IndexOf('#', StringComparison.Ordinal);
            if (hashIndex >= 0)
            {
                line = line[..hashIndex].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            var token = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (DohAddressValidator.TryNormalizeAddress(token, out var normalized, out _)
                && seen.Add(normalized))
            {
                results.Add(normalized);
            }
        }

        return results;
    }
}
