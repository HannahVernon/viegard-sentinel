using System.Net;

namespace Viegard.Application.Policy;

/// <summary>Pre-parsed protected CIDR list.  Malformed candidate addresses fail closed.</summary>
public sealed class ProtectedAddressList
{
    private readonly IReadOnlyList<CidrRange> _ranges;

    public ProtectedAddressList(IEnumerable<string> cidrs)
    {
        ArgumentNullException.ThrowIfNull(cidrs);

        var ranges = new List<CidrRange>();
        foreach (var cidr in cidrs)
        {
            if (!TryParseCidr(cidr, out var range, out var failure))
            {
                throw new ArgumentException($"Invalid protected CIDR '{cidr}': {failure}", nameof(cidrs));
            }

            ranges.Add(range);
        }

        _ranges = ranges;
    }

    /// <summary>
    /// Returns true when an address is protected.  Invalid or unparseable
    /// candidate input is treated as protected so it can never be auto-blocked.
    /// </summary>
    public bool IsProtected(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)
            || !IPAddress.TryParse(ip.Trim(), out var address))
        {
            return true;
        }

        address = NormalizeAddress(address);
        return _ranges.Any(range => range.Contains(address));
    }

    internal static bool TryParseCidr(string? cidr, out string? failure) =>
        TryParseCidr(cidr, out _, out failure);

    private static bool TryParseCidr(string? cidr, out CidrRange range, out string? failure)
    {
        range = default;
        failure = null;

        if (string.IsNullOrWhiteSpace(cidr))
        {
            failure = "CIDR value is empty.";
            return false;
        }

        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2)
        {
            failure = "CIDR value must contain one address and one prefix length.";
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var address))
        {
            failure = "address is not parseable.";
            return false;
        }

        if (!int.TryParse(parts[1], out var prefixLength))
        {
            failure = "prefix length is not an integer.";
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
            if (prefixLength >= 96 && prefixLength <= 128)
            {
                prefixLength -= 96;
            }
        }
        else
        {
            address = NormalizeAddress(address);
        }

        var bytes = address.GetAddressBytes();
        var maxPrefixLength = bytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            failure = $"prefix length must be between 0 and {maxPrefixLength}.";
            return false;
        }

        range = new CidrRange(address, prefixLength);
        return true;
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
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
