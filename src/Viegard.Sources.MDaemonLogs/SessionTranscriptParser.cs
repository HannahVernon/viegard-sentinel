using System.Globalization;
using System.Net;
using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs;

/// <summary>Parser for MDaemon session-transcript logs (SMTP, IMAP, POP3, Screening).</summary>
public static class SessionTranscriptParser
{
    public static MDaemonParsedLine? Parse(string? line, MDaemonLogKind logKind)
    {
        if (line is null || IsBannerOrSeparator(line))
        {
            return null;
        }

        if (!TryParsePrefix(line, out var reportedAt, out var message))
        {
            return null;
        }

        return Classify(logKind, line, reportedAt, message);
    }

    public static bool IsBannerOrSeparator(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.Trim();
        return trimmed.StartsWith("START Event Log", StringComparison.Ordinal)
            || trimmed.StartsWith("Event Time/Date", StringComparison.Ordinal)
            || AllSame(trimmed, '-');
    }

    private static MDaemonParsedLine Classify(
        MDaemonLogKind logKind,
        string line,
        DateTimeOffset reportedAt,
        string message)
    {
        if (TryExtractAfter(message, "Session ", ';', out var sessionId))
        {
            return Parsed(logKind, MDaemonEventKind.SessionLine, line, reportedAt, sessionId: sessionId.Trim());
        }

        if (message.Contains("Accepting ", StringComparison.OrdinalIgnoreCase)
            && message.Contains(" connection from ", StringComparison.OrdinalIgnoreCase)
            && TryExtractAcceptingConnection(message, out var remoteIp, out var port))
        {
            return Parsed(logKind, MDaemonEventKind.ConnectionAccepted, line, reportedAt, remoteIp, port);
        }

        if (message.Contains("535 5.7.8 Authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return Parsed(logKind, MDaemonEventKind.AuthenticationFailed, line, reportedAt);
        }

        if (message.Contains("Host screening refused connection", StringComparison.OrdinalIgnoreCase)
            && TryExtractHostScreening(message, out remoteIp, out port, out var reason))
        {
            return Parsed(logKind, MDaemonEventKind.ScreeningBlocked, line, reportedAt, remoteIp, port, reason);
        }

        if (message.Contains("Location Screening: IP ", StringComparison.OrdinalIgnoreCase)
            && message.Contains("connection blocked", StringComparison.OrdinalIgnoreCase)
            && TryExtractLocationScreening(message, out remoteIp, out port, out reason))
        {
            return Parsed(logKind, MDaemonEventKind.ScreeningBlocked, line, reportedAt, remoteIp, port, reason);
        }

        return Parsed(logKind, MDaemonEventKind.Other, line, reportedAt);
    }

    private static bool TryParsePrefix(string line, out DateTimeOffset reportedAt, out string message)
    {
        reportedAt = default;
        message = string.Empty;

        var firstColon = line.IndexOf(": ", StringComparison.Ordinal);
        if (firstColon < 0)
        {
            return false;
        }

        var prefix = line[..firstColon];
        if (!DateTime.TryParseExact(
                prefix,
                "ddd yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        reportedAt = ToLocalOffset(parsed);
        var remaining = line[(firstColon + 2)..];
        if (remaining.Length >= 4
            && char.IsDigit(remaining[0])
            && char.IsDigit(remaining[1])
            && remaining[2] == ':'
            && remaining[3] == ' ')
        {
            remaining = remaining[4..];
        }

        message = remaining;
        return true;
    }

    private static bool TryExtractAcceptingConnection(string message, out string? remoteIp, out int? port)
    {
        remoteIp = null;
        port = null;

        if (!TryExtractIpPortAfter(message, "from ", out remoteIp, out _))
        {
            return false;
        }

        TryExtractIpPortAfter(message, " to ", out _, out port);
        return true;
    }

    private static bool TryExtractHostScreening(
        string message,
        out string? remoteIp,
        out int? port,
        out string? reason)
    {
        remoteIp = null;
        port = null;
        reason = null;

        TryExtractIpPortAfter(message, "connection to ", out _, out port);
        var bracket = message.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
        {
            return false;
        }

        var close = message.IndexOf(']', bracket + 1);
        if (close <= bracket + 1)
        {
            return false;
        }

        var endpoint = message[(bracket + 1)..close];
        if (!TryParseEndpoint(endpoint, out remoteIp, out _))
        {
            return false;
        }

        if (TryExtractAfter(message, "(matched to line ", ')', out var matched))
        {
            reason = "matched to line " + matched;
        }

        return true;
    }

    private static bool TryExtractLocationScreening(
        string message,
        out string? remoteIp,
        out int? port,
        out string? reason)
    {
        remoteIp = null;
        port = null;
        reason = null;

        var marker = "Location Screening: IP ";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = message.IndexOf(' ', start);
        if (end <= start)
        {
            return false;
        }

        var ip = message[start..end];
        if (!IsIp(ip))
        {
            return false;
        }

        remoteIp = ip;

        var matched = "matched to ";
        var matchedStart = message.IndexOf(matched, end, StringComparison.OrdinalIgnoreCase);
        var blockedStart = message.IndexOf("; connection blocked", StringComparison.OrdinalIgnoreCase);
        if (matchedStart >= 0 && blockedStart > matchedStart)
        {
            reason = message[(matchedStart + matched.Length)..blockedStart];
        }

        var portMarker = "; port ";
        var portStart = message.IndexOf(portMarker, StringComparison.OrdinalIgnoreCase);
        if (portStart >= 0)
        {
            portStart += portMarker.Length;
            TryParseIntToken(message, portStart, out port);
        }

        return true;
    }

    private static bool TryExtractIpPortAfter(string message, string marker, out string? ip, out int? port)
    {
        ip = null;
        port = null;

        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var start = markerIndex + marker.Length;
        var end = FindTokenEnd(message, start);
        if (end <= start)
        {
            return false;
        }

        return TryParseEndpoint(message[start..end], out ip, out port);
    }

    private static bool TryParseEndpoint(string endpoint, out string? ip, out int? port)
    {
        ip = null;
        port = null;

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var candidateIp = endpoint[..colon].Trim('[', ']');
        if (!IsIp(candidateIp))
        {
            return false;
        }

        ip = candidateIp;
        if (int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
        {
            port = parsedPort;
        }

        return true;
    }

    private static bool TryExtractAfter(string message, string marker, char terminator, out string value)
    {
        value = string.Empty;
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = message.IndexOf(terminator, start);
        if (end < 0)
        {
            return false;
        }

        value = message[start..end];
        return value.Length > 0;
    }

    private static int FindTokenEnd(string value, int start)
    {
        var end = start;
        while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] != ']')
        {
            end++;
        }

        return end;
    }

    private static void TryParseIntToken(string value, int start, out int? parsed)
    {
        parsed = null;
        var end = start;
        while (end < value.Length && char.IsDigit(value[end]))
        {
            end++;
        }

        if (end > start && int.TryParse(value[start..end], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            parsed = port;
        }
    }

    private static bool IsIp(string value) => IPAddress.TryParse(value, out _);

    private static bool AllSame(string value, char expected)
    {
        foreach (var c in value)
        {
            if (c != expected)
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static MDaemonParsedLine Parsed(
        MDaemonLogKind logKind,
        MDaemonEventKind eventKind,
        string message,
        DateTimeOffset reportedAt,
        string? remoteIp = null,
        int? port = null,
        string? reason = null,
        string? sessionId = null) => new()
        {
            LogKind = logKind,
            EventKind = eventKind,
            RemoteIp = remoteIp,
            Port = port,
            Reason = reason,
            SessionId = sessionId,
            Message = message,
            ReportedAt = reportedAt,
        };

    private static DateTimeOffset ToLocalOffset(DateTime parsed)
    {
        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }
}
