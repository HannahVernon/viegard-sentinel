using Viegard.AdminApi.Components.Shared;
using Viegard.Application.Stores;

namespace Viegard.AdminApi.Tests;

public sealed class SortHeaderTests
{
    [Fact]
    public void BuildHref_applies_default_direction_and_preserves_encoded_filters_without_cursor()
    {
        var href = SortHeader.BuildHref(
            "/events",
            "source",
            currentSortKey: null,
            currentDirection: null,
            defaultDirection: SortDirection.Asc,
            new Dictionary<string, string?>
            {
                ["q"] = "ref=after ship&x",
                ["take"] = "100",
                ["cursor"] = Guid.NewGuid().ToString(),
                ["prev"] = Guid.NewGuid().ToString(),
                ["page"] = "3",
                ["empty"] = " ",
            });

        Assert.Equal("/events?q=ref%3Dafter%20ship%26x&take=100&sort=source&dir=asc", href);
    }

    [Fact]
    public void BuildHref_toggles_active_column_and_places_query_before_fragment()
    {
        var href = SortHeader.BuildHref(
            "/signatures#list",
            "name",
            currentSortKey: "name",
            currentDirection: SortDirection.Asc,
            defaultDirection: SortDirection.Asc,
            new Dictionary<string, string?>
            {
                ["q"] = "bot",
                ["sort"] = "updated",
                ["dir"] = "desc",
            });

        Assert.Equal("/signatures?q=bot&sort=name&dir=desc#list", href);
    }
}
