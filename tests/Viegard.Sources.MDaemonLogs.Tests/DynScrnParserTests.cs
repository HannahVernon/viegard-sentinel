using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs.Tests;

public sealed class DynScrnParserTests
{
    [Fact]
    public void Parses_blocking_ip_line()
    {
        var parsed = DynScrnParser.Parse(
            "260818 005841591 I [1002FA89] 0x415041A1 Block List: IMAP: Blocking IP:203.0.113.20 (connected 16 times within 6 minutes)");

        Assert.NotNull(parsed);
        Assert.Equal(MDaemonEventKind.IpBlocked, parsed.EventKind);
        Assert.Equal("203.0.113.20", parsed.RemoteIp);
        Assert.Equal("connected 16 times within 6 minutes", parsed.Reason);
        Assert.Equal("1002FA89", parsed.SessionId);
        Assert.Equal(2026, parsed.ReportedAt?.Year);
    }

    [Fact]
    public void Parses_management_add_blocked_item_line()
    {
        var parsed = DynScrnParser.Parse(
            "260818 005841591 I [1002FA89] 0x41502010 Mgmt Add: Blocked Item: IP:203.0.113.20 Comment:\"connected 16 times within 6 minutes\"");

        Assert.NotNull(parsed);
        Assert.Equal(MDaemonEventKind.IpBlocked, parsed.EventKind);
        Assert.Equal("203.0.113.20", parsed.RemoteIp);
        Assert.Equal("connected 16 times within 6 minutes", parsed.Reason);
    }

    [Fact]
    public void Parses_access_refused_as_noise()
    {
        var parsed = DynScrnParser.Parse(
            "260818 005841615 I [00000041] 0x415040C1 Block List: Refusing IMAP access: IP:203.0.113.20 VIA:198.51.100.21");

        Assert.NotNull(parsed);
        Assert.Equal(MDaemonEventKind.AccessRefused, parsed.EventKind);
        Assert.Equal("203.0.113.20", parsed.RemoteIp);
        Assert.True(parsed.IsNoise);
    }

    [Fact]
    public void Parses_trusted_ip_as_ignorable_noise()
    {
        var parsed = DynScrnParser.Parse(
            "260818 000408427 I [00000045] 0x41507015 TrustedIP: AS: found IP:192.168.0.20 VIA:192.168.*.*");

        Assert.NotNull(parsed);
        Assert.Equal(MDaemonEventKind.Other, parsed.EventKind);
        Assert.Equal("192.168.0.20", parsed.RemoteIp);
        Assert.True(parsed.IsNoise);
    }

    [Fact]
    public void Garbage_input_returns_null()
    {
        Assert.Null(DynScrnParser.Parse("not a Dynamic Screening log line"));
    }

    [Fact]
    public void Sanitized_dynscrn_fixture_lines_parse()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "dynscrn.log"));

        foreach (var line in lines.Where(l => l.Length > 0))
        {
            Assert.NotNull(DynScrnParser.Parse(line));
        }
    }
}
