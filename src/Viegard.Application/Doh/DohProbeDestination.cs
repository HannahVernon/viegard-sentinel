using System.Net;
using System.Net.Sockets;
using Viegard.Application.Policy;

namespace Viegard.Application.Doh;

/// <summary>
/// Decides whether a feed-supplied address may be contacted by the canary probe.
/// Feed content is untrusted, so the probe must not be steerable at internal
/// services (the probe client accepts any certificate).
/// </summary>
public static class DohProbeDestination
{
    public static bool IsAllowed(string? address, ProtectedAddressList? protectedList)
    {
        if (string.IsNullOrWhiteSpace(address)
            || address.Contains('/', StringComparison.Ordinal)
            || !IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (!IsPubliclyRoutable(ip))
        {
            return false;
        }

        return protectedList is null || !protectedList.IsProtected(ip.ToString());
    }

    private static bool IsPubliclyRoutable(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var v6 = ip.GetAddressBytes();
            var linkLocal = v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80;
            var uniqueLocal = (v6[0] & 0xFE) == 0xFC;
            var multicast = v6[0] == 0xFF;
            return !(linkLocal || uniqueLocal || multicast);
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 0
            || b[0] == 10
            || b[0] == 127
            || (b[0] == 100 && (b[1] & 0xC0) == 64)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && (b[1] & 0xF0) == 16)
            || (b[0] == 192 && b[1] == 168)
            || b[0] >= 224);
    }
}
