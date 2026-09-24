using System.Text;
using Viegard.Application.Doh;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class DohBlocklistTests
{
    [Fact]
    public void Feed_parser_keeps_ipv4_and_cidr_drops_ipv6_and_comments()
    {
        const string payload =
            "# curated DoH IPv4 feed\n" +
            "1.1.1.1\n" +
            "8.8.8.8   # google\n" +
            "192.0.2.0/24\n" +
            ";comment-line\n" +
            "2606:4700:4700::1111\n" +
            "1.1.1.1\n";

        var parsed = DohFeedParser.ParseText(payload);

        Assert.Equal(["1.1.1.1", "8.8.8.8", "192.0.2.0/24"], parsed);
    }

    [Fact]
    public void Feed_parser_returns_empty_for_blank_payload()
    {
        Assert.Empty(DohFeedParser.ParseText("   \n\n"));
    }

    [Fact]
    public void Canary_query_builder_produces_wire_format_txt_query()
    {
        Assert.True(DohCanaryProbe.TryBuildTxtQuery("_doh_canary.example.com", out var query, out _));

        // Header (12) + labels (1+11 + 1+7 + 1+3) + root (1) + qtype (2) + qclass (2).
        Assert.Equal(41, query.Length);
        Assert.Equal(0x01, query[2]); // RD flag
        Assert.Equal(0x01, query[5]); // QDCOUNT low byte
        Assert.Equal(0x00, query[^4]);
        Assert.Equal(0x10, query[^3]); // QTYPE = 16 (TXT)
        Assert.Equal(0x00, query[^2]);
        Assert.Equal(0x01, query[^1]); // QCLASS = 1 (IN)
    }

    [Fact]
    public void Canary_query_builder_rejects_invalid_fqdn()
    {
        Assert.False(DohCanaryProbe.TryBuildTxtQuery("not-a-fqdn", out _, out _));
    }

    [Fact]
    public void Contains_token_matches_ascii_token_in_body()
    {
        var body = Encoding.ASCII.GetBytes("prefix-DEADBEEF-suffix");

        Assert.True(DohCanaryProbe.ContainsToken(body, "DEADBEEF"));
        Assert.False(DohCanaryProbe.ContainsToken(body, "CAFEBABE"));
        Assert.False(DohCanaryProbe.ContainsToken(body, string.Empty));
    }

    [Fact]
    public void Validator_accepts_defaults()
    {
        Assert.True(DohBlocklistSettingsValidator.TryValidate(new DohBlocklistSettings(), out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Validator_rejects_non_absolute_primary_feed_url()
    {
        var settings = new DohBlocklistSettings { PrimaryFeedUrl = "not-a-url" };

        Assert.False(DohBlocklistSettingsValidator.TryValidate(settings, out var error));
        Assert.Equal(DohBlocklistSettingsValidator.PrimaryFeedUrlError, error);
    }

    [Fact]
    public void Validator_rejects_out_of_range_probe_concurrency()
    {
        var settings = new DohBlocklistSettings { ProbeConcurrency = 0 };

        Assert.False(DohBlocklistSettingsValidator.TryValidate(settings, out var error));
        Assert.Equal(DohBlocklistSettingsValidator.ProbeConcurrencyError, error);
    }

    [Fact]
    public async Task Settings_store_seeds_then_enforces_optimistic_concurrency()
    {
        var store = new InMemoryDohBlocklistSettingsStore();
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        var seed = await store.UpdateAsync(new DohBlocklistSettings(), expectedRowVersion: 0, "hannah", now);
        Assert.True(seed.Succeeded);
        Assert.Equal(1, seed.Settings!.RowVersion);

        var update = await store.UpdateAsync(
            new DohBlocklistSettings { ApplyToRouters = true },
            expectedRowVersion: 1,
            "hannah",
            now);
        Assert.True(update.Succeeded);
        Assert.Equal(2, update.Settings!.RowVersion);
        Assert.True(update.Settings.ApplyToRouters);

        var stale = await store.UpdateAsync(new DohBlocklistSettings(), expectedRowVersion: 1, "hannah", now);
        Assert.False(stale.Succeeded);
        Assert.Equal(DohBlocklistSettingsSaveStatus.Conflict, stale.Status);
    }
}
