namespace Viegard.Application.Net;

/// <summary>
/// Parses and normalizes the optional per-token source-network list of a read-only app password.
/// An empty list means "no restriction".  Entries are bare addresses or CIDR ranges.
/// </summary>
public static class AppPasswordNetworks
{
    public const int MaxEntries = 16;

    public const int MaxStoredLength = 1024;

    public static bool TryParse(string? input, out IReadOnlyList<string> networks, out string? error)
    {
        networks = [];
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var entries = input
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count > MaxEntries)
        {
            error = $"At most {MaxEntries} allowed networks may be listed.";
            return false;
        }

        foreach (var entry in entries)
        {
            if (!CidrSet.TryParseEntry(entry, out var failure))
            {
                error = $"Allowed network '{Truncate(entry)}' is not a valid IP address or CIDR range: {failure}";
                return false;
            }
        }

        if (string.Join(',', entries).Length > MaxStoredLength)
        {
            error = "The allowed network list is too long.";
            return false;
        }

        networks = entries;
        return true;
    }

    /// <summary>Joins entries for storage in a single text column.</summary>
    public static string Join(IReadOnlyList<string> networks) => string.Join(',', networks);

    public static IReadOnlyList<string> Split(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64] + "...";
}
