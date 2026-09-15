using Viegard.AdminApi.Components.Shared;

namespace Viegard.AdminApi.Tests;

public sealed class KeysetPaginationTests
{
    [Fact]
    public void BuildHref_builds_first_link_without_cursor_prev_or_page()
    {
        var href = KeysetPagination.BuildHref(
            "/events",
            cursor: null,
            previous: null,
            take: 100,
            new Dictionary<string, string?>
            {
                ["q"] = "ref=after ship&x",
                ["sort"] = "source",
                ["dir"] = "asc",
                ["cursor"] = Guid.NewGuid().ToString(),
                ["prev"] = Guid.NewGuid().ToString(),
                ["page"] = "4",
                ["take"] = "25",
                ["empty"] = " ",
            });

        Assert.Equal("/events?take=100&q=ref%3Dafter%20ship%26x&sort=source&dir=asc", href);
    }

    [Fact]
    public void BuildHref_builds_keyset_links_and_preserves_filters()
    {
        var cursor = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var previous = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var href = KeysetPagination.BuildHref(
            "/audit",
            cursor,
            previous,
            take: 50,
            new Dictionary<string, string?>
            {
                ["q"] = "login",
                ["stage"] = "Admin",
                ["sort"] = "timestamp",
                ["dir"] = "desc",
            });

        Assert.Equal(
            "/audit?cursor=11111111-1111-1111-1111-111111111111&prev=22222222-2222-2222-2222-222222222222&take=50&q=login&stage=Admin&sort=timestamp&dir=desc",
            href);
    }

    [Fact]
    public void BuildPageHref_places_query_before_fragment_for_last_and_go_to_page()
    {
        var href = KeysetPagination.BuildPageHref(
            "/signatures#list",
            pageNumber: 12,
            take: 25,
            new Dictionary<string, string?>
            {
                ["q"] = "bot",
                ["sort"] = "name",
                ["dir"] = "asc",
            });

        Assert.Equal("/signatures?take=25&q=bot&sort=name&dir=asc&page=12#list", href);
    }

    [Fact]
    public void ActiveFilterParameters_filters_navigation_keys_for_forms()
    {
        var filters = KeysetPagination.ActiveFilterParameters(new Dictionary<string, string?>
        {
            ["q"] = "alpha",
            ["sort"] = "source",
            ["dir"] = "asc",
            ["take"] = "200",
            ["cursor"] = Guid.NewGuid().ToString(),
            ["prev"] = Guid.NewGuid().ToString(),
            ["page"] = "3",
            ["empty"] = " ",
        });

        Assert.Equal(
            [new KeyValuePair<string, string>("q", "alpha"), new KeyValuePair<string, string>("sort", "source"), new KeyValuePair<string, string>("dir", "asc")],
            filters);
    }
}
