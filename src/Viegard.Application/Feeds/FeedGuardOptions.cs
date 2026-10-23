using Microsoft.Extensions.Options;

namespace Viegard.Application.Feeds;

/// <summary>
/// Safety limits applied to third-party feed content (DoH and JetPack) before it
/// is reconciled into router address lists.  Feed content is untrusted: these
/// guards stop an over-broad, protected, oversized, or suddenly different feed
/// from reaching a router.
/// </summary>
public sealed class FeedGuardOptions
{
    public const string SectionName = "Viegard:FeedGuards";

    public const int DefaultMinIPv4PrefixLength = 16;
    public const int DefaultMinIPv6PrefixLength = 32;
    public const int DefaultDohMaxEntries = 20000;
    public const int DefaultJetPackMaxEntries = 5000;
    public const double DefaultMaxChangeRatio = 0.5;
    public const int DefaultMaxChangeRatioMinExistingEntries = 20;

    /// <summary>Broadest IPv4 CIDR accepted from a feed.  A /16 allows at most 65536 addresses per entry.</summary>
    public int MinIPv4PrefixLength { get; set; } = DefaultMinIPv4PrefixLength;

    /// <summary>Broadest IPv6 CIDR accepted from a feed.  Feeds are IPv4-only today; this applies if IPv6 entries are ever admitted.</summary>
    public int MinIPv6PrefixLength { get; set; } = DefaultMinIPv6PrefixLength;

    /// <summary>Largest DoH desired set that may be reconciled.  A larger set blocks the whole refresh.</summary>
    public int DohMaxEntries { get; set; } = DefaultDohMaxEntries;

    /// <summary>Largest JetPack desired set that may be reconciled.  A larger set blocks the whole refresh.</summary>
    public int JetPackMaxEntries { get; set; } = DefaultJetPackMaxEntries;

    /// <summary>
    /// Largest fraction of a router's existing list that one refresh may add plus
    /// remove.  A larger change is not applied and raises an audit warning.
    /// </summary>
    public double MaxChangeRatio { get; set; } = DefaultMaxChangeRatio;

    /// <summary>
    /// The change-ratio guard applies only when the router list already holds at
    /// least this many entries, so first population and small lists are not blocked.
    /// </summary>
    public int MaxChangeRatioMinExistingEntries { get; set; } = DefaultMaxChangeRatioMinExistingEntries;
}

public sealed class FeedGuardOptionsValidator : IValidateOptions<FeedGuardOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.MinIPv4PrefixLength is < 1 or > 32)
        {
            failures.Add("FeedGuards MinIPv4PrefixLength must be between 1 and 32; a /0 is never accepted.");
        }

        if (options.MinIPv6PrefixLength is < 1 or > 128)
        {
            failures.Add("FeedGuards MinIPv6PrefixLength must be between 1 and 128; a /0 is never accepted.");
        }

        if (options.DohMaxEntries <= 0)
        {
            failures.Add("FeedGuards DohMaxEntries must be positive.");
        }

        if (options.JetPackMaxEntries <= 0)
        {
            failures.Add("FeedGuards JetPackMaxEntries must be positive.");
        }

        if (double.IsNaN(options.MaxChangeRatio) || options.MaxChangeRatio is <= 0.0 or > 1.0)
        {
            failures.Add("FeedGuards MaxChangeRatio must be greater than 0 and at most 1.");
        }

        if (options.MaxChangeRatioMinExistingEntries < 0)
        {
            failures.Add("FeedGuards MaxChangeRatioMinExistingEntries must not be negative.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
