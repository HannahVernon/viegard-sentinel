using System.Net;
using System.Net.Sockets;
using System.Text;
using Viegard.Application.Policy;

namespace Viegard.Application.Feeds;

public enum FeedRejectReason
{
    Malformed,
    PrefixTooBroad,
    ProtectedRange,
}

public sealed record FeedRejection(string Entry, FeedRejectReason Reason);

public sealed record FeedFilterResult(
    IReadOnlyList<string> Accepted,
    IReadOnlyList<FeedRejection> Rejected)
{
    public string SummarizeRejections(int maxSamples = 5)
    {
        if (Rejected.Count == 0)
        {
            return string.Empty;
        }

        var counts = string.Join(
            ", ",
            Rejected.GroupBy(item => item.Reason).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));
        var samples = string.Join(
            ", ",
            Rejected.Take(maxSamples).Select(item => FeedAddressGuard.Sanitize(item.Entry)));
        return $"{counts}; samples: {samples}";
    }
}

/// <summary>Filters untrusted feed entries against the protected list and the minimum prefix length.</summary>
public static class FeedAddressGuard
{
    private const int MaxLoggedEntryLength = 64;

    public static FeedFilterResult Filter(
        IEnumerable<string> entries,
        ProtectedAddressList protectedList,
        FeedGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(protectedList);
        ArgumentNullException.ThrowIfNull(options);

        var accepted = new List<string>();
        var rejected = new List<FeedRejection>();
        foreach (var entry in entries)
        {
            var reason = Evaluate(entry, protectedList, options);
            if (reason is null)
            {
                accepted.Add(entry);
            }
            else
            {
                rejected.Add(new FeedRejection(entry, reason.Value));
            }
        }

        return new FeedFilterResult(accepted, rejected);
    }

    public static FeedRejectReason? Evaluate(string? entry, ProtectedAddressList protectedList, FeedGuardOptions options)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return FeedRejectReason.Malformed;
        }

        var text = entry.Trim();
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return FeedRejectReason.Malformed;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > maxPrefix))
        {
            return FeedRejectReason.Malformed;
        }

        var minPrefix = address.AddressFamily == AddressFamily.InterNetwork
            ? options.MinIPv4PrefixLength
            : options.MinIPv6PrefixLength;
        if (prefix == 0 || prefix < minPrefix)
        {
            return FeedRejectReason.PrefixTooBroad;
        }

        var protectedHit = slash < 0 ? protectedList.IsProtected(text) : protectedList.OverlapsProtected(text);
        return protectedHit ? FeedRejectReason.ProtectedRange : null;
    }

    /// <summary>
    /// Decides whether a router refresh changes too much of the existing list.
    /// Returns a detail string when the change must be blocked, otherwise null.
    /// </summary>
    public static string? EvaluateChangeRatio(int existingCount, int changeCount, FeedGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (existingCount < options.MaxChangeRatioMinExistingEntries
            || existingCount == 0
            || changeCount <= 0)
        {
            return null;
        }

        var ratio = (double)changeCount / existingCount;
        return ratio > options.MaxChangeRatio
            ? $"Refresh blocked by the change-ratio guard: {changeCount} changes against {existingCount} existing entries exceeds the limit of {options.MaxChangeRatio:0.##}."
            : null;
    }

    /// <summary>Strips control characters and bounds length so untrusted values are safe to log.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLoggedEntryLength));
        foreach (var ch in value)
        {
            if (builder.Length >= MaxLoggedEntryLength)
            {
                break;
            }

            builder.Append(char.IsControl(ch) ? '?' : ch);
        }

        return builder.ToString();
    }
}
