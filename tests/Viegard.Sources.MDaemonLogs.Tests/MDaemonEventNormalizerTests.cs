using System.Text.Json;
using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs.Tests;

public sealed class MDaemonEventNormalizerTests
{
    private readonly MDaemonEventNormalizer _normalizer = new(new MDaemonSourceOptions());

    private static RawObservation Observation() => new()
    {
        Id = Guid.NewGuid(),
        SourceId = "mdaemon:logs",
        SourceType = "mdaemon",
        ObservedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
        PayloadReference = "mdaemon/test/1",
    };

    private static string Payload(MDaemonLogKind logKind, string line, string fileName = "MDaemon-2026-08-18-SMTP-(in).log") =>
        JsonSerializer.Serialize(new MDaemonLineDto
        {
            SchemaVersion = 1,
            LogKind = logKind,
            FileName = fileName,
            LineText = line,
            CapturedAt = new DateTimeOffset(2026, 8, 18, 12, 30, 0, TimeSpan.Zero),
        }, MDaemonJson.SerializerOptions);

    [Fact]
    public void Valid_session_lines_normalize_and_auth_failure_inherits_session_ip()
    {
        var session = _normalizer.Normalize(
            Observation(),
            Payload(MDaemonLogKind.SmtpIn, "Tue 2026-08-18 00:00:48.993: 05: Session 09012313; child 0001"));
        var accept = _normalizer.Normalize(
            Observation(),
            Payload(MDaemonLogKind.SmtpIn, "Tue 2026-08-18 00:00:48.993: 05: Accepting SMTP connection from 203.0.113.10:45584 to 192.168.0.10:25"));
        var auth = _normalizer.Normalize(
            Observation(),
            Payload(MDaemonLogKind.SmtpIn, "Tue 2026-08-18 12:10:38.432: 03: --> 535 5.7.8 Authentication failed"));

        Assert.True(session.Succeeded);
        Assert.True(accept.Succeeded);
        Assert.True(auth.Succeeded);
        var payload = Assert.IsType<MDaemonLogEvent>(auth.Event!.Payload);
        Assert.Equal(MDaemonEventKind.AuthenticationFailed, payload.EventKind);
        Assert.Equal("203.0.113.10", payload.RemoteIp);
        Assert.Equal("09012313", payload.SessionId);
        Assert.Contains(auth.Event.Entities, e => e.Kind == EntityKind.IpAddress && e.Value == "203.0.113.10");
    }

    [Fact]
    public void Dynamic_screening_block_normalizes_with_ip_entity()
    {
        var result = _normalizer.Normalize(
            Observation(),
            Payload(
                MDaemonLogKind.DynamicScreening,
                "260818 005841591 I [1002FA89] 0x415041A1 Block List: IMAP: Blocking IP:203.0.113.20 (connected 16 times within 6 minutes)",
                "DynScrn-2026-08-18.log"));

        Assert.True(result.Succeeded);
        var payload = Assert.IsType<MDaemonLogEvent>(result.Event!.Payload);
        Assert.Equal(MDaemonEventKind.IpBlocked, payload.EventKind);
        Assert.Equal("203.0.113.20", payload.RemoteIp);
        Assert.Equal("connected 16 times within 6 minutes", payload.Reason);
        Assert.Contains(result.Event.Entities, e => e.Kind == EntityKind.IpAddress && e.Value == "203.0.113.20");
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
        var payload = """
            { "schemaVersion": 2, "logKind": "smtpIn", "fileName": "x.log", "lineText": "x", "capturedAt": "2026-08-18T12:00:00-05:00" }
            """;

        Assert.False(_normalizer.Normalize(Observation(), payload).Succeeded);
    }

    [Fact]
    public void Malformed_line_text_fails()
    {
        var result = _normalizer.Normalize(Observation(), Payload(MDaemonLogKind.SmtpIn, "garbage input"));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Trusted_ip_noise_fails_when_noise_is_excluded()
    {
        var result = _normalizer.Normalize(
            Observation(),
            Payload(
                MDaemonLogKind.DynamicScreening,
                "260818 000408427 I [00000045] 0x41507015 TrustedIP: AS: found IP:192.168.0.20 VIA:192.168.*.*",
                "DynScrn-2026-08-18.log"));

        Assert.False(result.Succeeded);
    }
}
