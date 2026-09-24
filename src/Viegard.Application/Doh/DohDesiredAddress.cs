using System.Net;
using System.Net.Sockets;

namespace Viegard.Application.Doh;

/// <summary>A candidate DoH server IPv4 address (or CIDR) drawn from the curated feeds.</summary>
public sealed record DohDesiredAddress
{
    public string Address { get; init; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }
}

public static class DohAddressValidator
{
    public const string AddressError = "DoH candidate addresses must be IPv4 addresses or IPv4 CIDR ranges.";

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
            if (!IPAddress.TryParse(addressText, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork
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

        if (!IPAddress.TryParse(candidate, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            error = AddressError;
            return false;
        }

        normalized = parsed.ToString();
        error = string.Empty;
        return true;
    }
}

/// <summary>Persistence port for the merged DoH candidate-address snapshot.</summary>
public interface IDohDesiredAddressStore
{
    ValueTask<IReadOnlyList<DohDesiredAddress>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<DohDesiredAddressRefreshResult> ReplaceSnapshotAsync(
        IReadOnlyCollection<string> addresses,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);
}

public sealed record DohDesiredAddressRefreshResult(
    int Added,
    int Updated,
    int Removed,
    int CurrentCount);
