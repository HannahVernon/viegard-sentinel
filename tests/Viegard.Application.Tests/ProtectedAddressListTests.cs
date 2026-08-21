using Microsoft.Extensions.Options;
using Viegard.Application.Policy;

namespace Viegard.Application.Tests;

public sealed class ProtectedAddressListTests
{
    private readonly ProtectedAddressList _list = new(new PolicyOptions().ProtectedCidrs);

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.50")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("0.1.2.3")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:192.168.1.50")]
    public void Default_protected_cidrs_match_expected_families(string ip)
    {
        Assert.True(_list.IsProtected(ip));
    }

    [Theory]
    [InlineData("198.51.100.10")]
    [InlineData("2001:db8::1")]
    public void Public_documentation_addresses_are_not_protected_by_default(string ip)
    {
        Assert.False(_list.IsProtected(ip));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("198.51.100.10 extra")]
    public void Malformed_candidate_addresses_fail_closed_as_protected(string ip)
    {
        Assert.True(_list.IsProtected(ip));
    }

    [Fact]
    public void Policy_options_validator_rejects_invalid_cidrs_and_confidence_ordering()
    {
        var validator = new PolicyOptionsValidator();
        var result = validator.Validate(
            Options.DefaultName,
            new PolicyOptions
            {
                ProtectedCidrs = ["bad-cidr"],
                AiReviewConfidence = 0.95,
                AiActionConfidence = 0.9,
            });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failures);
        var failures = result.Failures!;
        Assert.Contains(failures, failure => failure.Contains("ProtectedCidrs", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("AiReviewConfidence", StringComparison.Ordinal));
    }
}
