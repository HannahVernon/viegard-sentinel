using Viegard.Application.Feeds;
using Viegard.Application.Policy;

namespace Viegard.Application.Tests;

public sealed class FeedAddressGuardTests
{
    private static readonly ProtectedAddressList Protected = new(["10.0.0.0/8", "192.0.2.0/24", "fc00::/7"]);

    [Theory]
    [InlineData("203.0.113.7", null)]
    [InlineData("203.0.113.0/24", null)]
    [InlineData("203.0.0.0/16", null)]
    [InlineData("203.0.0.0/15", FeedRejectReason.PrefixTooBroad)]
    [InlineData("0.0.0.0/0", FeedRejectReason.PrefixTooBroad)]
    [InlineData("10.4.5.6", FeedRejectReason.ProtectedRange)]
    [InlineData("192.0.2.0/25", FeedRejectReason.ProtectedRange)]
    [InlineData("192.0.0.0/16", FeedRejectReason.ProtectedRange)]
    [InlineData("not-an-address", FeedRejectReason.Malformed)]
    [InlineData("203.0.113.0/33", FeedRejectReason.Malformed)]
    [InlineData("2001:db8::/16", FeedRejectReason.PrefixTooBroad)]
    [InlineData("fd00::1", FeedRejectReason.ProtectedRange)]
    [InlineData("2001:db8::/32", null)]
    public void Evaluate_applies_prefix_and_protected_rules(string entry, FeedRejectReason? expected)
    {
        Assert.Equal(expected, FeedAddressGuard.Evaluate(entry, Protected, new FeedGuardOptions()));
    }

    [Fact]
    public void Summary_sanitizes_control_characters()
    {
        var result = FeedAddressGuard.Filter(["bad\r\nline"], Protected, new FeedGuardOptions());

        Assert.DoesNotContain('\n', result.SummarizeRejections());
        Assert.DoesNotContain('\r', result.SummarizeRejections());
    }

    [Theory]
    [InlineData(100, 50, false)]
    [InlineData(100, 51, true)]
    [InlineData(10, 10, false)]
    [InlineData(0, 500, false)]
    public void Change_ratio_guard_blocks_large_changes_on_established_lists(int existing, int changes, bool blocked)
    {
        var options = new FeedGuardOptions { MaxChangeRatio = 0.5, MaxChangeRatioMinExistingEntries = 20 };

        Assert.Equal(blocked, FeedAddressGuard.EvaluateChangeRatio(existing, changes, options) is not null);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(33, false)]
    [InlineData(16, true)]
    public void Options_validation_rejects_zero_and_out_of_range_prefixes(int prefix, bool valid)
    {
        var result = new FeedGuardOptionsValidator().Validate(null, new FeedGuardOptions { MinIPv4PrefixLength = prefix });

        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public void Protected_list_overlap_detects_containment_in_both_directions()
    {
        Assert.True(Protected.OverlapsProtected("10.1.0.0/16"));
        Assert.True(Protected.OverlapsProtected("0.0.0.0/1"));
        Assert.False(Protected.OverlapsProtected("11.0.0.0/8"));
        Assert.True(Protected.OverlapsProtected("garbage/8"));
    }
}
