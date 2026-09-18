using Viegard.Application.Auth;

namespace Viegard.Application.Tests;

public sealed class AppPasswordTokenFormatTests
{
    [Fact]
    public void Generate_produces_parseable_token_with_matching_hash()
    {
        var generated = AppPasswordTokenFormat.Generate();

        Assert.StartsWith(AppPasswordTokenFormat.Prefix, generated.Token, StringComparison.Ordinal);
        Assert.True(AppPasswordTokenFormat.TryParseLookupKey(generated.Token, out var lookupKey));
        Assert.Equal(generated.LookupKey, lookupKey);
        Assert.Equal(AppPasswordTokenFormat.LookupKeyLength, lookupKey.Length);
        Assert.Matches("^[0-9a-f]{16}$", lookupKey);
        Assert.Equal(64, generated.SecretHash.Length);
        Assert.True(AppPasswordTokenFormat.Matches(generated.Token, generated.SecretHash));
    }

    [Fact]
    public void Generated_tokens_are_unique()
    {
        var first = AppPasswordTokenFormat.Generate();
        var second = AppPasswordTokenFormat.Generate();

        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.LookupKey, second.LookupKey);
        Assert.NotEqual(first.SecretHash, second.SecretHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("viegard_ro_")]
    [InlineData("viegard_ro_0123456789abcdef")]
    [InlineData("viegard_ro_0123456789ABCDEFsecretpart")]
    [InlineData("viegard_ro_0123456789abcdeXsecretpart")]
    [InlineData("other_prefix_0123456789abcdefsecretpart")]
    [InlineData("Bearer viegard_ro_0123456789abcdefsecretpart")]
    public void TryParseLookupKey_rejects_malformed_tokens(string? token)
    {
        Assert.False(AppPasswordTokenFormat.TryParseLookupKey(token, out var lookupKey));
        Assert.Equal(string.Empty, lookupKey);
    }

    [Fact]
    public void Matches_rejects_a_different_token_with_the_same_lookup_key()
    {
        var generated = AppPasswordTokenFormat.Generate();
        var forged = generated.Token[..^4] + "AAAA";

        Assert.True(AppPasswordTokenFormat.TryParseLookupKey(forged, out var lookupKey));
        Assert.Equal(generated.LookupKey, lookupKey);
        Assert.False(AppPasswordTokenFormat.Matches(forged, generated.SecretHash));
    }
}
