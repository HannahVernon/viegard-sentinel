using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Viegard.Domain.Events;

namespace Viegard.Sources.Syslog;

/// <summary>
/// Parses nginx access-log lines in the standard combined format, with
/// optional Viegard extensions (" host=$host rt=$request_time").  Never
/// throws; unparseable lines return null so the caller can fall back to a
/// generic syslog event.  All extracted values remain untrusted.
/// </summary>
public static partial class NginxAccessLogParser
{
    public static HttpRequestEvent? Parse(string line) => Parse(line, out _);

    /// <summary>
    /// Parses <paramref name="line"/>.  When the line matches the log format but its
    /// remote address is not a valid IP address, returns null with
    /// <paramref name="malformedAddress"/> set and counts the drop; the offending
    /// text is never logged or stored because it is attacker-controlled.
    /// </summary>
    public static HttpRequestEvent? Parse(string line, out bool malformedAddress)
    {
        ArgumentNullException.ThrowIfNull(line);
        malformedAddress = false;

        var match = CombinedPattern().Match(line.Trim());
        if (!match.Success)
        {
            return null;
        }

        if (!TryCanonicalizeAddress(match.Groups["ip"].Value, out var remoteAddress))
        {
            malformedAddress = true;
            Interlocked.Increment(ref _malformedAddressDropped);
            SyslogMetrics.MalformedAddress.Add(1);
            return null;
        }

        DateTimeOffset? requestedAt = DateTimeOffset.TryParseExact(
            match.Groups["time"].Value,
            "dd/MMM/yyyy:HH:mm:ss zzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedTime)
            ? parsedTime
            : null;

        string? method = null;
        string? uri = null;
        string? protocol = null;
        var request = match.Groups["request"].Value;
        if (!string.IsNullOrEmpty(request) && request != "-")
        {
            // Malformed/hostile request lines may not have three parts; take
            // what is present without guessing.
            var parts = request.Split(' ', 3);
            method = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null;
            uri = parts.Length > 1 ? parts[1] : null;
            protocol = parts.Length > 2 ? parts[2] : null;
        }

        return new HttpRequestEvent
        {
            RemoteAddress = remoteAddress,
            RemoteUser = Dash(match.Groups["user"].Value),
            RequestedAt = requestedAt,
            Method = method,
            Uri = uri,
            Protocol = protocol,
            StatusCode = int.TryParse(match.Groups["status"].Value, out var status) ? status : null,
            BodyBytes = long.TryParse(match.Groups["bytes"].Value, out var bytes) ? bytes : null,
            Referrer = Dash(match.Groups["referer"].Value),
            UserAgent = Dash(match.Groups["ua"].Value),
            Host = match.Groups["host"].Success ? match.Groups["host"].Value : null,
            RequestSeconds = match.Groups["rt"].Success
                && double.TryParse(match.Groups["rt"].Value, CultureInfo.InvariantCulture, out var rt)
                ? rt
                : null,
        };
    }

    private static long _malformedAddressDropped;

    /// <summary>Count of well-formed log lines dropped because the remote address was not a valid IP address.</summary>
    public static long MalformedAddressDropped => Interlocked.Read(ref _malformedAddressDropped);

    /// <summary>
    /// Accepts only a literal IPv4 (four dotted decimal parts) or IPv6 address without a
    /// zone id, and returns the canonical text form (IPv4-mapped IPv6 becomes IPv4).
    /// </summary>
    public static bool TryCanonicalizeAddress(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(value)
            || value.Length > 45
            || value.Contains('%', StringComparison.Ordinal)
            || !IPAddress.TryParse(value, out var ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork && value.Count(c => c == '.') != 3)
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        canonical = ip.ToString();
        return true;
    }

    private static string? Dash(string value) => value is "-" or "" ? null : value;

    // nginx escapes '"' and control characters inside logged variables
    // (\x22 escaping), so quote-delimited fields cannot be broken out of.
    [GeneratedRegex(
        """^(?<ip>\S+) - (?<user>\S+) \[(?<time>[^\]]+)\] "(?<request>[^"]*)" (?<status>\d{3}) (?<bytes>\d+|-)(?: "(?<referer>[^"]*)" "(?<ua>[^"]*)")?(?: host=(?<host>\S+))?(?: rt=(?<rt>[\d.]+))?\s*$""")]
    private static partial Regex CombinedPattern();
}
