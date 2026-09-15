using System.Net;

namespace Viegard.Application.Net;

/// <summary>
/// A pre-parsed set of IP addresses and CIDR ranges with prefix matching.
/// Entries may be bare addresses ("192.0.2.10", "2001:db8::7") or CIDR
/// ranges ("192.168.0.0/16", "fc00::/7").  IPv4-mapped IPv6 input is
/// normalized to IPv4 on both sides.  Matching is pure; callers decide
/// their own fail-open/fail-closed semantics for unparseable candidates.
/// </summary>
public sealed class CidrSet
{
    private readonly IReadOnlyList<CidrRange> _ranges;

    public CidrSet(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ranges = new List<CidrRange>();
        foreach (var entry in entries)
        {
            if (!TryParseEntry(entry, out var range, out var failure))
            {
                throw new ArgumentException($"Invalid address or CIDR '{entry}': {failure}", nameof(entries));
            }

            ranges.Add(range);
        }

        _ranges = ranges;
    }

    public int Count => _ranges.Count;

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);
        return _ranges.Any(range => range.Contains(normalized));
    }

    /// <summary>Validates one entry (bare address or CIDR) without building a set.</summary>
    public static bool TryParseEntry(string? entry, out string? failure) =>
        TryParseEntry(entry, out _, out failure);

    private static bool TryParseEntry(string? entry, out CidrRange range, out string? failure)
    {
        range = default;
        failure = null;

        if (string.IsNullOrWhiteSpace(entry))
        {
            failure = "value is empty.";
            return false;
        }

        var trimmed = entry.Trim();
        var slash = trimmed.IndexOf('/');

        string addressPart;
        int? explicitPrefix = null;
        if (slash < 0)
        {
            addressPart = trimmed;
        }
        else
        {
            addressPart = trimmed[..slash];
            if (!int.TryParse(trimmed[(slash + 1)..], out var parsedPrefix))
            {
                failure = "prefix length is not an integer.";
                return false;
            }

            explicitPrefix = parsedPrefix;
        }

        if (!IPAddress.TryParse(addressPart, out var address))
        {
            failure = "address is not parseable.";
            return false;
        }

        var prefixLength = explicitPrefix ?? int.MaxValue;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
            if (explicitPrefix is >= 96 and <= 128)
            {
                prefixLength = explicitPrefix.Value - 96;
            }
        }

        var maxPrefixLength = address.GetAddressBytes().Length * 8;
        if (explicitPrefix is null)
        {
            prefixLength = maxPrefixLength;
        }

        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            failure = $"prefix length must be between 0 and {maxPrefixLength}.";
            return false;
        }

        range = new CidrRange(address, prefixLength);
        return true;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private readonly record struct CidrRange(IPAddress Network, int PrefixLength)
    {
        public bool Contains(IPAddress address)
        {
            var candidateBytes = address.GetAddressBytes();
            var networkBytes = Network.GetAddressBytes();
            if (candidateBytes.Length != networkBytes.Length)
            {
                return false;
            }

            var fullBytes = PrefixLength / 8;
            for (var i = 0; i < fullBytes; i++)
            {
                if (candidateBytes[i] != networkBytes[i])
                {
                    return false;
                }
            }

            var remainingBits = PrefixLength % 8;
            if (remainingBits == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (candidateBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
        }
    }
}
