namespace Viegard.Domain.Events;

/// <summary>
/// Canonical, namespaced kind names for event payloads, used by the
/// EventKind custom-signature target.  Names are "source-family/kind" so a
/// Prefix match such as "mdaemon/" selects an entire source family, while a
/// Contains match such as "mdaemon/ScreeningBlocked" selects one kind.  New
/// payload types become matchable by adding one mapping here; unmapped
/// payloads return an empty string and never match.
/// </summary>
public static class EventKindName
{
    public static string Of(EventPayload? payload) => payload switch
    {
        MDaemonLogEvent mdaemon => $"mdaemon/{mdaemon.EventKind}",
        HttpRequestEvent => "http/request",
        SyslogEvent => "syslog/message",
        MailMessageEvent => "mail/message",
        AdminAuthEvent admin => $"admin/{admin.Kind}",
        MalformedRecordPayload => "malformed/record",
        _ => string.Empty,
    };
}
