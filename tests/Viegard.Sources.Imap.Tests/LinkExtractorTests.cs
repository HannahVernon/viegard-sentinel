namespace Viegard.Sources.Imap.Tests;

public sealed class LinkExtractorTests
{
    [Fact]
    public void Extracts_urls_from_text()
    {
        var links = LinkExtractor.Extract("Visit https://example.com/a and http://example.org/b.", null);

        Assert.Contains("https://example.com/a", links);
        Assert.Contains("http://example.org/b", links);
    }

    [Fact]
    public void Extracts_hrefs_from_html()
    {
        var links = LinkExtractor.Extract(null, "<p><a href=\"https://example.com/x?y=1\">x</a></p>");
        Assert.Contains("https://example.com/x?y=1", links);
    }

    [Fact]
    public void Deduplicates_across_bodies()
    {
        var links = LinkExtractor.Extract(
            "https://example.com/same",
            "<a href='https://example.com/same'>same</a>");

        Assert.Single(links);
    }

    [Fact]
    public void Trims_trailing_punctuation()
    {
        var links = LinkExtractor.Extract("Click https://example.com/page.", null);
        Assert.Contains("https://example.com/page", links);
    }

    [Fact]
    public void Caps_link_count_against_hostile_mail()
    {
        var body = string.Join(' ', Enumerable.Range(0, 500).Select(i => $"https://example.com/{i}"));
        var links = LinkExtractor.Extract(body, null);

        Assert.Equal(LinkExtractor.MaxLinks, links.Count);
    }

    [Fact]
    public void Ignores_non_http_schemes()
    {
        var links = LinkExtractor.Extract("javascript:alert(1) file:///etc/passwd ftp://example.com", null);
        Assert.Empty(links);
    }
}
