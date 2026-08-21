namespace Viegard.Sources.Syslog;

/// <summary>
/// Serialization contract between the UDP listener and the normalizer: the
/// raw payload of a syslog observation is this record as JSON.  Raw content
/// is untrusted observed data.
/// </summary>
public sealed record SyslogDatagramDto
{
    public required int SchemaVersion { get; init; }

    /// <summary>Transport-level sender address (the only trusted origin identity).</summary>
    public required string PeerIp { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The datagram text, decoded as UTF-8 with replacement characters.</summary>
    public required string Raw { get; init; }
}
