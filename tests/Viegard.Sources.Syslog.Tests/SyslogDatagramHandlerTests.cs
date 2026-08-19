using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace Viegard.Sources.Syslog.Tests;

public sealed class SyslogDatagramHandlerTests
{
    private static readonly IPAddress Allowed = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress NotAllowed = IPAddress.Parse("192.0.2.99");

    private static SyslogSourceOptions Options(int rate = 500, int maxBytes = 8192)
    {
        var options = new SyslogSourceOptions
        {
            Enabled = true,
            MaxDatagramBytes = maxBytes,
            MaxDatagramsPerSourcePerSecond = rate,
        };
        options.AllowedSources.Add(Allowed.ToString());
        return options;
    }

    [Fact]
    public void Accepted_datagram_becomes_observed_item()
    {
        var handler = new SyslogDatagramHandler(Options(), "syslog:udp-5514");

        var result = handler.Handle(Allowed, Encoding.UTF8.GetBytes("<190>Aug 19 11:45:01 swag nginx_access: line"));

        Assert.NotNull(result.Item);
        Assert.Null(result.DropReason);
        Assert.Equal("syslog:udp-5514", result.Item.Observation.SourceId);
        Assert.Contains("nginx_access", result.Item.RawPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void Disallowed_source_is_dropped_and_counted()
    {
        var handler = new SyslogDatagramHandler(Options(), "syslog:udp-5514");

        var result = handler.Handle(NotAllowed, Encoding.UTF8.GetBytes("anything"));

        Assert.Null(result.Item);
        Assert.Equal(DatagramDropReason.SourceNotAllowed, result.DropReason);
        Assert.Equal(1, handler.DroppedNotAllowed);
    }

    [Fact]
    public void Oversized_datagram_is_dropped()
    {
        var handler = new SyslogDatagramHandler(Options(maxBytes: 128), "syslog:udp-5514");

        var result = handler.Handle(Allowed, new byte[129]);

        Assert.Equal(DatagramDropReason.TooLarge, result.DropReason);
        Assert.Equal(1, handler.DroppedTooLarge);
    }

    [Fact]
    public void Rate_cap_drops_excess_then_recovers_after_refill()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var handler = new SyslogDatagramHandler(Options(rate: 2), "syslog:udp-5514", time);
        var payload = Encoding.UTF8.GetBytes("x");

        Assert.NotNull(handler.Handle(Allowed, payload).Item);
        Assert.NotNull(handler.Handle(Allowed, payload).Item);
        Assert.Equal(DatagramDropReason.RateLimited, handler.Handle(Allowed, payload).DropReason);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(handler.Handle(Allowed, payload).Item);
    }

    [Fact]
    public void Hostile_bytes_never_throw()
    {
        var handler = new SyslogDatagramHandler(Options(), "syslog:udp-5514");
        var hostileBytes = new byte[] { 0xFF, 0xFE, 0x00, 0xC3, 0x28, 0xA0, 0xA1 };

        var result = handler.Handle(Allowed, hostileBytes);

        Assert.NotNull(result.Item);
    }
}
