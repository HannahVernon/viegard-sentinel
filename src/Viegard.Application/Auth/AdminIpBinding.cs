using System.Net;
using Viegard.Application.Net;

namespace Viegard.Application.Auth;

public sealed record AdminIpBindingDecision(bool Allowed, bool Matched, string Mode);

public static class AdminIpBinding
{
    public static AdminIpBindingDecision Evaluate(string mode, string boundAddress, IPAddress currentAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundAddress);
        ArgumentNullException.ThrowIfNull(currentAddress);

        var normalizedMode = AdminIpBindingModes.Normalize(mode);
        if (!IPAddress.TryParse(boundAddress, out var originalAddress))
        {
            return new AdminIpBindingDecision(false, Matched: false, normalizedMode);
        }

        var matched = normalizedMode switch
        {
            AdminIpBindingModes.Strict => SameAddress(originalAddress, currentAddress),
            AdminIpBindingModes.Subnet => SameConfiguredSubnet(originalAddress, currentAddress),
            AdminIpBindingModes.LogOnly => SameAddress(originalAddress, currentAddress),
            _ => false,
        };

        return normalizedMode == AdminIpBindingModes.LogOnly
            ? new AdminIpBindingDecision(Allowed: true, matched, normalizedMode)
            : new AdminIpBindingDecision(matched, matched, normalizedMode);
    }

    private static bool SameAddress(IPAddress original, IPAddress current) =>
        Normalize(original).Equals(Normalize(current));

    private static bool SameConfiguredSubnet(IPAddress original, IPAddress current)
    {
        var normalizedOriginal = Normalize(original);
        var normalizedCurrent = Normalize(current);
        var prefix = normalizedOriginal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 24 : 64;
        var cidr = new CidrSet([$"{normalizedOriginal}/{prefix}"]);
        return cidr.Contains(normalizedCurrent);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
