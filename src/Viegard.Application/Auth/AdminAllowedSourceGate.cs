using System.Net;
using Viegard.Application.Net;

namespace Viegard.Application.Auth;

public sealed class AdminAllowedSourceGate
{
    private readonly CidrSet _allowedSources;

    public AdminAllowedSourceGate(IEnumerable<string> allowedSources)
    {
        ArgumentNullException.ThrowIfNull(allowedSources);
        _allowedSources = new CidrSet(allowedSources);
    }

    public bool IsAllowed(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        var normalized = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        return IPAddress.IsLoopback(normalized)
            || (_allowedSources.Count > 0 && _allowedSources.Contains(normalized));
    }
}
