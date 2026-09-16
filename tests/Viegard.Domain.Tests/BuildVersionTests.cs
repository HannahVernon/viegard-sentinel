using Viegard.Domain.Health;

namespace Viegard.Domain.Tests;

public sealed class BuildVersionTests
{
    [Fact]
    public void Parse_extracts_full_commit_sha_and_nine_character_short_sha()
    {
        const string Sha = "f2f974293d366eb28e9738b1050397592b4721b4";

        var version = BuildVersion.Parse("1.0.0+" + Sha);

        Assert.Equal("1.0.0+" + Sha, version.InformationalVersion);
        Assert.Equal(Sha, version.CommitSha);
        Assert.Equal("f2f974293", version.ShortSha);
    }

    [Fact]
    public void Parse_preserves_informational_version_without_sha()
    {
        var version = BuildVersion.Parse("1.0.0");

        Assert.Equal("1.0.0", version.InformationalVersion);
        Assert.Null(version.CommitSha);
        Assert.Null(version.ShortSha);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("garbage+not-a-sha", "garbage+not-a-sha")]
    [InlineData("1.0.0+1234", "1.0.0+1234")]
    public void Parse_handles_missing_or_garbage_input_without_sha(string? input, string expectedVersion)
    {
        var version = BuildVersion.Parse(input);

        Assert.Equal(expectedVersion, version.InformationalVersion);
        Assert.Null(version.CommitSha);
        Assert.Null(version.ShortSha);
    }
}
