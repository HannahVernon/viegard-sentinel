using System.Text.Json;
using Viegard.Domain.Events;
using Viegard.Persistence.Postgres;

namespace Viegard.Persistence.Postgres.Tests;

/// <summary>
/// Guards the persisted JSON format: every payload type must round-trip
/// polymorphically through the discriminator, because rows store payloads as
/// jsonb of the abstract EventPayload.
/// </summary>
public sealed class PayloadSerializationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static EventPayload RoundTrip(EventPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        return JsonSerializer.Deserialize<EventPayload>(json, Json)!;
    }

    [Fact]
    public void Mail_message_payload_round_trips()
    {
        var payload = new MailMessageEvent
        {
            AccountId = "acct",
            Folder = "INBOX",
            Uid = 42,
            MessageId = "<x@example.com>",
            From = [new MailAddressInfo { Address = "s@example.com", DisplayName = "S" }],
            Subject = "Hi",
            TextBody = "body",
            Links = ["https://example.com"],
            Attachments = [new AttachmentInfo { FileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 10 }],
        };

        var restored = Assert.IsType<MailMessageEvent>(RoundTrip(payload));
        // Field-wise comparison: record equality is reference-based for
        // collection-typed properties.
        Assert.Equal(payload.AccountId, restored.AccountId);
        Assert.Equal(payload.Folder, restored.Folder);
        Assert.Equal(payload.Uid, restored.Uid);
        Assert.Equal(payload.MessageId, restored.MessageId);
        Assert.Equal(payload.From, restored.From);
        Assert.Equal(payload.Subject, restored.Subject);
        Assert.Equal(payload.TextBody, restored.TextBody);
        Assert.Equal(payload.Links, restored.Links);
        Assert.Equal(payload.Attachments, restored.Attachments);
    }

    [Fact]
    public void Http_request_payload_round_trips()
    {
        var payload = new HttpRequestEvent
        {
            RemoteAddress = "203.0.113.7",
            Method = "GET",
            Uri = "/.env",
            StatusCode = 404,
            UserAgent = "zgrab/0.x",
            Host = "blog.example.com",
            RequestSeconds = 0.01,
        };

        var restored = Assert.IsType<HttpRequestEvent>(RoundTrip(payload));
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void Syslog_payload_round_trips()
    {
        var payload = new SyslogEvent
        {
            PeerIp = "192.0.2.10",
            Facility = 23,
            Severity = 6,
            Tag = "sshd",
            Message = "Failed password",
        };

        var restored = Assert.IsType<SyslogEvent>(RoundTrip(payload));
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void Mdaemon_payload_round_trips()
    {
        var payload = new MDaemonLogEvent
        {
            LogKind = MDaemonLogKind.DynamicScreening,
            EventKind = MDaemonEventKind.IpBlocked,
            RemoteIp = "203.0.113.20",
            Reason = "connected 16 times within 6 minutes",
            SessionId = "1002FA89",
            Message = "sanitized MDaemon log line",
        };

        var restored = Assert.IsType<MDaemonLogEvent>(RoundTrip(payload));
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void Malformed_record_payload_round_trips()
    {
        var payload = new MalformedRecordPayload { Reason = "bad", RawSample = "x" };
        var restored = Assert.IsType<MalformedRecordPayload>(RoundTrip(payload));
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void Discriminator_is_present_in_serialized_form()
    {
        var json = JsonSerializer.Serialize<EventPayload>(
            new SyslogEvent { PeerIp = "192.0.2.10", Message = "m" }, Json);

        Assert.Contains("\"$payloadType\":\"syslog\"", json, StringComparison.Ordinal);
    }
}

public sealed class DatabaseOptionsValidatorTests
{
    private readonly DatabaseOptionsValidator _validator = new();

    [Fact]
    public void Valid_options_pass()
    {
        var options = new DatabaseOptions { Host = "viegard-db" };
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Missing_host_fails()
    {
        Assert.True(_validator.Validate(null, new DatabaseOptions()).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Invalid_port_fails(int port)
    {
        var options = new DatabaseOptions { Host = "h", Port = port };
        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Empty_password_secret_name_fails()
    {
        var options = new DatabaseOptions { Host = "h", PasswordSecretName = "" };
        Assert.True(_validator.Validate(null, options).Failed);
    }
}
