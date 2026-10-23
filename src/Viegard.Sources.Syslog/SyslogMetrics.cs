using System.Diagnostics.Metrics;

namespace Viegard.Sources.Syslog;

/// <summary>Metrics for the syslog source.  Instruments carry no attacker-controlled tags.</summary>
internal static class SyslogMetrics
{
    private static readonly Meter Meter = new("Viegard.Sources.Syslog");

    public static readonly Counter<long> MalformedAddress = Meter.CreateCounter<long>(
        "viegard.syslog.nginx_malformed_address_dropped",
        description: "Nginx access-log lines dropped because the remote address was not a valid IP address.");
}