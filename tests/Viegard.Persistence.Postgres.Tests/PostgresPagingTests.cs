using Viegard.Persistence.Postgres.Stores;

namespace Viegard.Persistence.Postgres.Tests;

public sealed class PostgresPagingTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData(@"a\b%c_d", @"a\\b\%c\_d")]
    public void EscapeLike_escapes_only_like_metacharacters(string value, string expected) =>
        Assert.Equal(expected, PostgresPaging.EscapeLike(value));
}
