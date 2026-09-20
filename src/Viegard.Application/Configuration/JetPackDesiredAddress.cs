namespace Viegard.Application.Configuration;

public sealed record JetPackDesiredAddress
{
    public string Address { get; init; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }
}

public static class JetPackDesiredAddressValidator
{
    public const string AddressError = "JetPack desired addresses must be IPv4 addresses or IPv4 CIDR ranges.";

    public static bool TryValidate(JetPackDesiredAddress address, out string error)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!TryNormalizeAddress(address.Address, out _, out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeAddress(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = AddressError;
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Contains('/'))
        {
            var slash = candidate.IndexOf('/', StringComparison.Ordinal);
            var addressText = candidate[..slash];
            var prefixText = candidate[(slash + 1)..];
            if (!System.Net.IPAddress.TryParse(addressText, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || !int.TryParse(prefixText, out var prefixLength)
                || prefixLength is < 0 or > 32)
            {
                error = AddressError;
                return false;
            }

            normalized = $"{address}/{prefixLength}";
            error = string.Empty;
            return true;
        }

        if (!System.Net.IPAddress.TryParse(candidate, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            error = AddressError;
            return false;
        }

        normalized = parsed.ToString();
        error = string.Empty;
        return true;
    }
}

/// <summary>Persistence port for the JetPack desired-address snapshot.</summary>
public interface IJetPackDesiredAddressStore
{
    ValueTask<IReadOnlyList<JetPackDesiredAddress>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<JetPackDesiredAddressRefreshResult> ReplaceSnapshotAsync(
        IReadOnlyCollection<string> addresses,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);
}

public sealed record JetPackDesiredAddressRefreshResult(
    int Added,
    int Updated,
    int Removed,
    int CurrentCount);
