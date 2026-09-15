using System.Text.Json;
using Viegard.Application.Telemetry;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Components;

public sealed record PayloadField(string Name, string Value);

public static class AdminText
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ShortId(Guid id) => id.ToString("N")[..12];

    /// <summary>
    /// Compact human-readable rendering of an age/duration, e.g. "0.8 s",
    /// "42 s", "4 m 12 s", "3 h 24 m", "2 d 5 h".  Negative values (clock
    /// skew) clamp to "0 s".
    /// </summary>
    public static string Age(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0 s";
        }

        if (value.TotalSeconds < 10)
        {
            return value.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
        }

        if (value.TotalMinutes < 1)
        {
            return ((int)value.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s";
        }

        if (value.TotalHours < 1)
        {
            return $"{(int)value.TotalMinutes} m {value.Seconds} s";
        }

        if (value.TotalDays < 1)
        {
            return $"{(int)value.TotalHours} h {value.Minutes} m";
        }

        return $"{(int)value.TotalDays} d {value.Hours} h";
    }

    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("u");

    public static string Utc(DateTimeOffset? value) => value is null ? "" : Utc(value.Value);

    public static string Limit(string? value, int maxChars = 2_000)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safeMax = Math.Clamp(maxChars, 1, 8_192);
        return value.Length <= safeMax ? value : value[..safeMax] + "... [truncated]";
    }

    public static string OneLine(string? value, int maxChars = 160) =>
        Limit((value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '), maxChars);

    public static string LightCss(TrafficLight light) => light switch
    {
        TrafficLight.Green => "badge badge-green",
        TrafficLight.Amber => "badge badge-amber",
        TrafficLight.Red => "badge badge-red",
        _ => "badge",
    };

    public static string OutcomeCss(DecisionOutcome outcome) => outcome switch
    {
        DecisionOutcome.Permit => "badge badge-green",
        DecisionOutcome.DryRun => "badge badge-blue",
        DecisionOutcome.RequireApproval => "badge badge-amber",
        DecisionOutcome.Deny => "badge badge-red",
        _ => "badge",
    };

    public static string PayloadTypeName(EventPayload payload) => payload.GetType().Name;

    public static string PayloadSummary(EventPayload payload) => payload switch
    {
        HttpRequestEvent http => OneLine($"{http.Method ?? ""} {http.Uri ?? ""} {http.StatusCode?.ToString() ?? ""}".Trim(), 220),
        MailMessageEvent mail => OneLine($"From {JoinMail(mail.From)} Subject {mail.Subject ?? ""}".Trim(), 220),
        SyslogEvent syslog => OneLine($"{syslog.Tag ?? "syslog"}: {syslog.Message}", 220),
        MDaemonLogEvent mdaemon => OneLine($"{mdaemon.EventKind}: {mdaemon.Message}", 220),
        AdminAuthEvent admin => OneLine($"{admin.Kind} for {admin.Username}", 220),
        MalformedRecordPayload malformed => OneLine($"Malformed: {malformed.Reason}", 220),
        _ => PayloadTypeName(payload),
    };

    public static IReadOnlyList<PayloadField> PayloadFields(EventPayload payload) => PayloadFields(payload, Utc);

    public static IReadOnlyList<PayloadField> PayloadFields(EventPayload payload, Func<DateTimeOffset, string> formatTimestamp)
    {
        ArgumentNullException.ThrowIfNull(formatTimestamp);
        return payload switch
        {
            HttpRequestEvent http =>
            [
                Field("Remote address", http.RemoteAddress),
                Field("Remote user", http.RemoteUser),
                Field("Requested at", Timestamp(http.RequestedAt, formatTimestamp)),
                Field("Method", http.Method),
                Field("URI", http.Uri),
                Field("Protocol", http.Protocol),
                Field("Status code", http.StatusCode?.ToString()),
                Field("Body bytes", http.BodyBytes?.ToString()),
                Field("Referrer", http.Referrer),
                Field("User-Agent", http.UserAgent),
                Field("Host", http.Host),
                Field("Request seconds", http.RequestSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            MailMessageEvent mail =>
            [
                Field("Account", mail.AccountId),
                Field("Folder", mail.Folder),
                Field("UID", mail.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Field("Message-Id", mail.MessageId),
                Field("From", JoinMail(mail.From)),
                Field("Reply-To", JoinMail(mail.ReplyTo)),
                Field("To", JoinMail(mail.To)),
                Field("Cc", JoinMail(mail.Cc)),
                Field("Subject", mail.Subject),
                Field("Sent at", Timestamp(mail.SentAt, formatTimestamp)),
                Field("Text body", mail.TextBody),
                Field("HTML body", mail.HtmlBody),
                Field("Links", string.Join(", ", mail.Links)),
                Field("Attachments", string.Join(", ", mail.Attachments.Select(a => $"{a.FileName} ({a.ContentType}, {a.SizeBytes})"))),
            ],
            SyslogEvent syslog =>
            [
                Field("Peer IP", syslog.PeerIp),
                Field("Facility", syslog.Facility?.ToString()),
                Field("Severity", syslog.Severity?.ToString()),
                Field("Claimed hostname", syslog.ClaimedHostname),
                Field("Tag", syslog.Tag),
                Field("Message", syslog.Message),
                Field("Reported at", Timestamp(syslog.ReportedAt, formatTimestamp)),
            ],
            MDaemonLogEvent mdaemon =>
            [
                Field("Log kind", mdaemon.LogKind.ToString()),
                Field("Event kind", mdaemon.EventKind.ToString()),
                Field("Remote IP", mdaemon.RemoteIp),
                Field("Port", mdaemon.Port?.ToString()),
                Field("Reason", mdaemon.Reason),
                Field("Session ID", mdaemon.SessionId),
                Field("Message", mdaemon.Message),
                Field("Reported at", Timestamp(mdaemon.ReportedAt, formatTimestamp)),
            ],
            AdminAuthEvent admin =>
            [
                Field("Kind", admin.Kind.ToString()),
                Field("Username", admin.Username),
                Field("Remote address", admin.RemoteAddress),
                Field("User-Agent", admin.UserAgent),
                Field("Occurred at", formatTimestamp(admin.OccurredAt)),
            ],
            MalformedRecordPayload malformed =>
            [
                Field("Reason", malformed.Reason),
                Field("Raw sample", malformed.RawSample),
            ],
            _ => [],
        };
    }

    public static string PayloadJson(EventPayload payload) =>
        Limit(JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions));

    private static PayloadField Field(string name, string? value) => new(name, Limit(value));

    private static string Timestamp(DateTimeOffset? value, Func<DateTimeOffset, string> formatTimestamp) =>
        value is null ? string.Empty : formatTimestamp(value.Value);

    private static string JoinMail(IEnumerable<MailAddressInfo> addresses) =>
        string.Join(", ", addresses.Select(a => string.IsNullOrWhiteSpace(a.DisplayName)
            ? a.Address
            : $"{a.DisplayName} <{a.Address}>"));
}
