using System.Globalization;
using System.Text.RegularExpressions;

namespace Viegard.Sources.Syslog;

/// <summary>Parsed syslog envelope.  Every field originates from the datagram and is untrusted.</summary>
public sealed record SyslogEnvelope
{
    public int? Facility { get; init; }

    public int? Severity { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// True when the timestamp came from a zone-less format (classic RFC
    /// 3164): the wall-clock digits are preserved as claimed, but the sender's
    /// UTC offset is unknown, so the value must not be treated as an instant.
    /// </summary>
    public bool TimestampIsZoneless { get; init; }

    public string? ClaimedHostname { get; init; }

    public string? Tag { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// Parses RFC 3164 (BSD), RFC 5424, and the common "RFC 3164 with ISO 8601
/// timestamp" hybrid (emitted by RouterOS and rsyslog forwarders) syslog
/// envelopes.  Never throws: anything unparseable degrades to a message-only
/// envelope so hostile or malformed datagrams still become inspectable
/// events.
/// </summary>
public static partial class SyslogEnvelopeParser
{
    public static SyslogEnvelope Parse(string raw, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(raw);

        int? facility = null;
        int? severity = null;
        var rest = raw;

        var priMatch = PriPattern().Match(rest);
        if (priMatch.Success && int.TryParse(priMatch.Groups["pri"].Value, out var pri) && pri is >= 0 and <= 191)
        {
            facility = pri / 8;
            severity = pri % 8;
            rest = rest[priMatch.Length..];
        }

        // RFC 5424: VERSION SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID SP [SD] MSG
        var rfc5424 = Rfc5424Pattern().Match(rest);
        if (rfc5424.Success)
        {
            DateTimeOffset? ts = DateTimeOffset.TryParse(
                rfc5424.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed5424)
                ? parsed5424
                : null;

            return new SyslogEnvelope
            {
                Facility = facility,
                Severity = severity,
                Timestamp = ts,
                ClaimedHostname = Nil(rfc5424.Groups["host"].Value),
                Tag = Nil(rfc5424.Groups["app"].Value),
                Message = rfc5424.Groups["msg"].Value,
            };
        }

        // RFC 3164 layout with an ISO 8601 timestamp (RouterOS "iso8601"
        // remote-log format, rsyslog forward format):
        // TIMESTAMP SP HOSTNAME SP TAG[pid]: MSG
        var isoHybrid = IsoHybridPattern().Match(rest);
        if (isoHybrid.Success
            && DateTimeOffset.TryParse(
                isoHybrid.Groups["ts"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsedIso))
        {
            var hasOffset = HasExplicitOffset(isoHybrid.Groups["ts"].Value);
            return new SyslogEnvelope
            {
                Facility = facility,
                Severity = severity,
                Timestamp = parsedIso,
                TimestampIsZoneless = !hasOffset,
                ClaimedHostname = isoHybrid.Groups["host"].Value,
                Tag = string.IsNullOrEmpty(isoHybrid.Groups["tag"].Value) ? null : isoHybrid.Groups["tag"].Value,
                Message = isoHybrid.Groups["msg"].Value,
            };
        }

        // RFC 3164: TIMESTAMP(MMM d HH:mm:ss) SP HOSTNAME SP TAG[pid]: MSG
        var rfc3164 = Rfc3164Pattern().Match(rest);
        if (rfc3164.Success)
        {
            DateTimeOffset? ts = null;
            if (DateTimeOffset.TryParseExact(
                    rfc3164.Groups["ts"].Value.Replace("  ", " ", StringComparison.Ordinal),
                    "MMM d HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    // The format carries no zone; parse deterministically
                    // (independent of the host's time zone) and flag the
                    // ambiguity so consumers do not treat it as an instant.
                    DateTimeStyles.AssumeUniversal,
                    out var parsed3164))
            {
                // RFC 3164 timestamps carry no year; assume the receive year
                // (handling December/January wrap by proximity).
                var candidate = parsed3164.AddYears(receivedAt.Year - parsed3164.Year);
                if (candidate - receivedAt > TimeSpan.FromDays(180))
                {
                    candidate = candidate.AddYears(-1);
                }
                else if (receivedAt - candidate > TimeSpan.FromDays(180))
                {
                    candidate = candidate.AddYears(1);
                }

                ts = candidate;
            }

            return new SyslogEnvelope
            {
                Facility = facility,
                Severity = severity,
                Timestamp = ts,
                TimestampIsZoneless = ts is not null,
                ClaimedHostname = rfc3164.Groups["host"].Value,
                Tag = string.IsNullOrEmpty(rfc3164.Groups["tag"].Value) ? null : rfc3164.Groups["tag"].Value,
                Message = rfc3164.Groups["msg"].Value,
            };
        }

        return new SyslogEnvelope
        {
            Facility = facility,
            Severity = severity,
            Message = rest,
        };
    }

    private static string? Nil(string value) => value == "-" ? null : value;

    /// <summary>True when the ISO timestamp text carries Z or a +/-HH[:mm] offset.</summary>
    private static bool HasExplicitOffset(string timestamp) =>
        timestamp.EndsWith('Z')
        || timestamp.LastIndexOfAny(['+', '-']) > timestamp.IndexOf('T', StringComparison.Ordinal);

    [GeneratedRegex(@"^<(?<pri>\d{1,3})>")]
    private static partial Regex PriPattern();

    [GeneratedRegex(@"^1 (?<ts>\S+) (?<host>\S+) (?<app>\S+) (?<procid>\S+) (?<msgid>\S+) (?:\[.*?\]|-)\s?(?<msg>.*)$", RegexOptions.Singleline)]
    private static partial Regex Rfc5424Pattern();

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?) (?<host>\S+) (?:(?<tag>[^:\[\s]+)(?:\[\d+\])?: )?(?<msg>.*)$", RegexOptions.Singleline)]
    private static partial Regex IsoHybridPattern();

    [GeneratedRegex(@"^(?<ts>[A-Z][a-z]{2} [ \d]\d \d{2}:\d{2}:\d{2}) (?<host>\S+) (?:(?<tag>[^:\[\s]+)(?:\[\d+\])?: )?(?<msg>.*)$", RegexOptions.Singleline)]
    private static partial Regex Rfc3164Pattern();
}
