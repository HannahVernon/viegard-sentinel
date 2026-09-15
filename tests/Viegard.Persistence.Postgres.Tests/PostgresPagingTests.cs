using Viegard.Persistence.Postgres.Stores;

namespace Viegard.Persistence.Postgres.Tests;

public sealed class PostgresPagingTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData(@"a\b%c_d", @"a\\b\%c\_d")]
    public void EscapeLike_escapes_only_like_metacharacters(string value, string expected) =>
        Assert.Equal(expected, PostgresPaging.EscapeLike(value));

    [Theory]
    [InlineData(1, 25, 101, -1)]
    [InlineData(2, 25, 101, 24)]
    [InlineData(5, 25, 101, 99)]
    [InlineData(99, 25, 101, 99)]
    [InlineData(-4, 25, 101, -1)]
    public void BoundaryOffset_clamps_page_number_and_returns_exclusive_cursor_offset(
        int pageNumber,
        int pageSize,
        long totalCount,
        long expected) =>
        Assert.Equal(expected < 0 ? null : expected, PostgresPaging.BoundaryOffset(pageNumber, pageSize, totalCount));
}
