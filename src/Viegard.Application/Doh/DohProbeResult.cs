namespace Viegard.Application.Doh;

/// <summary>Outcome of a canary DoH probe against a single candidate address.</summary>
public enum DohProbeStatus
{
    /// <summary>Never probed yet.</summary>
    Unprobed = 0,

    /// <summary>HTTP 200 + application/dns-message + the canary token was present.</summary>
    Confirmed = 1,

    /// <summary>The endpoint responded but the response was not a compliant, token-bearing DoH answer.</summary>
    RespondedNonCompliant = 2,

    /// <summary>The endpoint actively refused (TLS reset, 4xx/5xx, or ICMP admin-prohibited).</summary>
    Refused = 3,

    /// <summary>The probe timed out or the network was unreachable.</summary>
    Timeout = 4,
}

/// <summary>
/// The most recent probe outcome for a candidate address.  The curated feeds are
/// the authority for inclusion; this record only annotates confirmation state and
/// is never the sole basis for removal.
/// </summary>
public sealed record DohProbeResult
{
    public string Address { get; init; } = string.Empty;

    public DohProbeStatus Status { get; init; } = DohProbeStatus.Unprobed;

    public int? HttpStatus { get; init; }

    public bool TokenMatched { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastProbedAt { get; init; }

    public DateTimeOffset? LastConfirmedAt { get; init; }

    public bool IsConfirmed => Status == DohProbeStatus.Confirmed;
}

/// <summary>Persistence port for per-address DoH probe results.</summary>
public interface IDohProbeResultStore
{
    ValueTask<IReadOnlyList<DohProbeResult>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts probe results grouped by status and HTTP status without loading every row.</summary>
    ValueTask<IReadOnlyList<DohProbeStatusCount>> CountByStatusAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(DohProbeResult result, CancellationToken cancellationToken = default);

    /// <summary>Deletes probe results for addresses no longer present in the desired set.</summary>
    ValueTask<int> PruneAsync(
        IReadOnlyCollection<string> keepAddresses,
        CancellationToken cancellationToken = default);
}
