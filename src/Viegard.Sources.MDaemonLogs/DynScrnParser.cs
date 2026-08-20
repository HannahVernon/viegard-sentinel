using System.Globalization;
using System.Net;
using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs;

/// <summary>Parser for MDaemon Dynamic Screening (DynScrn) compact log lines.</summary>
public static class DynScrnParser
{
    public static MDaemonParsedLine? Parse(string? line)
    {
        if (line is null || !TryParsePrefix(line, out var reportedAt, out _, out var sessionId, out var code, out var message))
        {
            return null;
        }

        var eventKind = MDaemonEventKind.Other;
        var isNoise = false;
        string? remoteIp = null;
        string? reason = null;

        if (code.Equals("0x41507015", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("TrustedIP:", StringComparison.OrdinalIgnoreCase))
        {
            eventKind = MDaemonEventKind.Other;
            isNoise = true;
            TryExtractIpAfter(message, "found IP:", out remoteIp);
        }
        else if (code.Equals("0x41502010", StringComparison.OrdinalIgnoreCase)
            && message.Contains("Blocked Item", StringComparison.OrdinalIgnoreCase))
        {
            eventKind = MDaemonEventKind.IpBlocked;
            TryExtractIpAfter(message, "IP:", out remoteIp);
            reason = ExtractQuotedAfter(message, "Comment:");
        }
        else if (message.Contains("Blocking IP:", StringComparison.OrdinalIgnoreCase))
        {
            eventKind = MDaemonEventKind.IpBlocked;
            TryExtractIpAfter(message, "Blocking IP:", out remoteIp);
            reason = ExtractParenthesizedReason(message);
        }
        else if (code.Equals("0x415040C1", StringComparison.OrdinalIgnoreCase)
            || code.Equals("0x415060C1", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Refusing ", StringComparison.OrdinalIgnoreCase))
        {
            eventKind = MDaemonEventKind.AccessRefused;
            isNoise = true;
            TryExtractIpAfter(message, "IP:", out remoteIp);
            reason = ExtractQuotedAfter(message, "LOC:");
        }

        return new MDaemonParsedLine
        {
            LogKind = MDaemonLogKind.DynamicScreening,
            EventKind = eventKind,
            RemoteIp = remoteIp,
            Reason = reason,
            SessionId = sessionId,
            Message = line,
            ReportedAt = reportedAt,
            IsNoise = isNoise,
        };
    }

    private static bool TryParsePrefix(
        string line,
        out DateTimeOffset reportedAt,
        out char severity,
        out string sessionId,
        out string code,
        out string message)
    {
        reportedAt = default;
        severity = '\0';
        sessionId = string.Empty;
        code = string.Empty;
        message = string.Empty;

        var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
        {
            return false;
        }

        if (!TryParseTimestamp(parts[0], parts[1], out reportedAt))
        {
            return false;
        }

        if (parts[2].Length != 1)
        {
            return false;
        }

        severity = parts[2][0];

        if (parts[3].Length < 3 || parts[3][0] != '[' || parts[3][^1] != ']')
        {
            return false;
        }

        sessionId = parts[3][1..^1];
        code = parts[4];
        message = parts[5];
        return code.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseTimestamp(string date, string time, out DateTimeOffset reportedAt)
    {
        reportedAt = default;
        if (date.Length != 6 || time.Length != 9)
        {
            return false;
        }

        if (!int.TryParse(date[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var yy)
            || !int.TryParse(date[2..4], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(date[4..6], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(time[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(time[2..4], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || !int.TryParse(time[4..6], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
            || !int.TryParse(time[6..9], NumberStyles.None, CultureInfo.InvariantCulture, out var millisecond))
        {
            return false;
        }

        try
        {
            var local = new DateTime(2000 + yy, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
            reportedAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryExtractIpAfter(string message, string marker, out string? ip)
    {
        ip = null;
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = start;
        while (end < message.Length
               && !char.IsWhiteSpace(message[end])
               && message[end] != ')'
               && message[end] != ';'
               && message[end] != ',')
        {
            end++;
        }

        if (end <= start)
        {
            return false;
        }

        var candidate = message[start..end].Trim('"');
        if (!IPAddress.TryParse(candidate, out _))
        {
            return false;
        }

        ip = candidate;
        return true;
    }

    private static string? ExtractQuotedAfter(string message, string marker)
    {
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        while (start < message.Length && char.IsWhiteSpace(message[start]))
        {
            start++;
        }

        if (start >= message.Length || message[start] != '"')
        {
            return null;
        }

        start++;
        var end = message.IndexOf('"', start);
        return end > start ? message[start..end] : null;
    }

    private static string? ExtractParenthesizedReason(string message)
    {
        var start = message.LastIndexOf('(');
        var end = message.LastIndexOf(')');
        return start >= 0 && end > start ? message[(start + 1)..end] : null;
    }
}
