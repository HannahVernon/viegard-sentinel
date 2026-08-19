using System.Globalization;
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
    public static HttpRequestEvent? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var match = CombinedPattern().Match(line.Trim());
        if (!match.Success)
        {
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
            RemoteAddress = match.Groups["ip"].Value,
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

    private static string? Dash(string value) => value is "-" or "" ? null : value;

    // nginx escapes '"' and control characters inside logged variables
    // (\x22 escaping), so quote-delimited fields cannot be broken out of.
    [GeneratedRegex(
        """^(?<ip>\S+) - (?<user>\S+) \[(?<time>[^\]]+)\] "(?<request>[^"]*)" (?<status>\d{3}) (?<bytes>\d+|-)(?: "(?<referer>[^"]*)" "(?<ua>[^"]*)")?(?: host=(?<host>\S+))?(?: rt=(?<rt>[\d.]+))?\s*$""")]
    private static partial Regex CombinedPattern();
}
