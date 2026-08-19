namespace Viegard.Sources.Syslog;

/// <summary>Configuration for the syslog UDP ingestion listener (D-0023).</summary>
public sealed class SyslogSourceOptions
{
    public const string SectionName = "Viegard:Sources:Syslog";

    /// <summary>The listener is off unless explicitly enabled.</summary>
    public bool Enabled { get; set; }

    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Non-privileged default; map from host port 514 in deployment if senders require it.</summary>
    public int Port { get; set; } = 5514;

    /// <summary>
    /// Source-IP allowlist (D-0023 guardrail).  Fail-closed: when the
    /// listener is enabled this list must be non-empty, and datagrams from
    /// any other address are dropped and counted.
    /// </summary>
    public IList<string> AllowedSources { get; } = [];

    /// <summary>Datagrams larger than this are dropped.</summary>
    public int MaxDatagramBytes { get; set; } = 8192;

    /// <summary>Per-source rate cap (token bucket, per second).</summary>
    public int MaxDatagramsPerSourcePerSecond { get; set; } = 500;

    /// <summary>Syslog tags treated as nginx access-log lines.</summary>
    public IList<string> NginxAccessTags { get; } = ["nginx_access"];
}
