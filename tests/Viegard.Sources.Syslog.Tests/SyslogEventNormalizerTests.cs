using System.Text.Json;
using Viegard.Domain.Events;

namespace Viegard.Sources.Syslog.Tests;

public sealed class SyslogEventNormalizerTests
{
    private static SyslogSourceOptions Options()
    {
        var options = new SyslogSourceOptions { Enabled = true };
        options.AllowedSources.Add("192.0.2.10");
        return options;
    }

    private readonly SyslogEventNormalizer _normalizer = new(Options());

    private static RawObservation Observation() => new()
    {
        Id = Guid.NewGuid(),
        SourceId = "syslog:udp-5514",
        ObservedAt = DateTimeOffset.UtcNow,
        PayloadReference = "syslog/192.0.2.10/1/1",
    };

    private static string Payload(string raw) => JsonSerializer.Serialize(new SyslogDatagramDto
    {
        SchemaVersion = 1,
        PeerIp = "192.0.2.10",
        ReceivedAt = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
        Raw = raw,
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Nginx_access_tag_produces_http_request_event()
    {
        var raw = "<190>Aug 19 11:45:01 swag nginx_access: 203.0.113.7 - - [19/Aug/2026:11:45:01 -0500] " +
                  "\"GET /.env HTTP/1.1\" 404 153 \"-\" \"zgrab/0.x\" host=blog.example.com rt=0.001";

        var result = _normalizer.Normalize(Observation(), Payload(raw));

        Assert.True(result.Succeeded);
        var http = Assert.IsType<HttpRequestEvent>(result.Event!.Payload);
        Assert.Equal("203.0.113.7", http.RemoteAddress);
        Assert.Equal("/.env", http.Uri);
        Assert.Equal(404, http.StatusCode);
        Assert.Equal("blog.example.com", http.Host);

        Assert.Contains(result.Event.Entities, e => e.Kind == EntityKind.IpAddress && e.Value == "203.0.113.7");
        Assert.Contains(result.Event.Entities, e => e.Kind == EntityKind.Uri && e.Value == "/.env");
        Assert.Contains(result.Event.Entities, e => e.Kind == EntityKind.UserAgent && e.Value == "zgrab/0.x");
    }

    [Fact]
    public void Unknown_tag_produces_generic_syslog_event()
    {
        var raw = "<38>Aug 19 11:45:01 router sshd[99]: Failed password for admin from 203.0.113.9";

        var result = _normalizer.Normalize(Observation(), Payload(raw));

        Assert.True(result.Succeeded);
        var syslog = Assert.IsType<SyslogEvent>(result.Event!.Payload);
        Assert.Equal("sshd", syslog.Tag);
        Assert.Equal("192.0.2.10", syslog.PeerIp);
        Assert.Contains(result.Event.Entities, e => e.Kind == EntityKind.IpAddress && e.Value == "192.0.2.10");
    }

    [Fact]
    public void Unparseable_nginx_line_falls_back_to_generic_event()
    {
        var raw = "<190>Aug 19 11:45:01 swag nginx_access: this is not an access log line";

        var result = _normalizer.Normalize(Observation(), Payload(raw));

        Assert.True(result.Succeeded);
        Assert.IsType<SyslogEvent>(result.Event!.Payload);
    }

    [Fact]
    public void Claimed_hostname_is_carried_but_origin_is_peer_ip()
    {
        var raw = "<13>Aug 19 11:45:01 forged-hostname tag: spoofing attempt";

        var result = _normalizer.Normalize(Observation(), Payload(raw));

        var syslog = Assert.IsType<SyslogEvent>(result.Event!.Payload);
        Assert.Equal("forged-hostname", syslog.ClaimedHostname);
        Assert.Equal("192.0.2.10", syslog.PeerIp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    public void Malformed_payloads_fail_closed(string payload)
    {
        Assert.False(_normalizer.Normalize(Observation(), payload).Succeeded);
    }

    [Fact]
    public void Wrong_schema_version_fails()
    {
        var payload = """{ "schemaVersion": 2, "peerIp": "192.0.2.10", "receivedAt": "2026-08-19T12:00:00Z", "raw": "x" }""";
        Assert.False(_normalizer.Normalize(Observation(), payload).Succeeded);
    }
}
