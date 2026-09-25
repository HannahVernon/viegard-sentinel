namespace Viegard.Application.Doh;

/// <summary>A single (status, HTTP-status) tally from the probe-result store.</summary>
public sealed record DohProbeStatusCount(DohProbeStatus Status, int? HttpStatus, int Count);

/// <summary>Count of refused probes carrying a specific HTTP status, or none.</summary>
public sealed record DohRefusedHttpStatusCount(int? HttpStatus, int Count);

/// <summary>
/// Aggregate probe-outcome counts captured at the end of one probe cycle.  Total
/// counts probed addresses only; CIDR feed entries are never probed and carry no
/// probe record.
/// </summary>
public sealed record DohProbeOutcomeSummary
{
    public DateTimeOffset GeneratedAt { get; init; }

    public int Total { get; init; }

    public int Unprobed { get; init; }

    public int Confirmed { get; init; }

    public int RespondedNonCompliant { get; init; }

    public int Refused { get; init; }

    public int Timeout { get; init; }

    /// <summary>Refused tally split by HTTP status; a null HttpStatus means no HTTP response (transport-level refusal).</summary>
    public IReadOnlyList<DohRefusedHttpStatusCount> RefusedByHttpStatus { get; init; } = [];

    /// <summary>Builds a summary from the per-(status, HTTP-status) tallies returned by the probe-result store.</summary>
    public static DohProbeOutcomeSummary FromCounts(
        IEnumerable<DohProbeStatusCount> counts,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var list = counts as IReadOnlyList<DohProbeStatusCount> ?? counts.ToList();

        int SumFor(DohProbeStatus status) => list.Where(c => c.Status == status).Sum(c => c.Count);

        var refusedByHttp = list
            .Where(c => c.Status == DohProbeStatus.Refused)
            .GroupBy(c => c.HttpStatus)
            .Select(g => new DohRefusedHttpStatusCount(g.Key, g.Sum(c => c.Count)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.HttpStatus ?? int.MaxValue)
            .ToList();

        return new DohProbeOutcomeSummary
        {
            GeneratedAt = generatedAt,
            Total = list.Sum(c => c.Count),
            Unprobed = SumFor(DohProbeStatus.Unprobed),
            Confirmed = SumFor(DohProbeStatus.Confirmed),
            RespondedNonCompliant = SumFor(DohProbeStatus.RespondedNonCompliant),
            Refused = SumFor(DohProbeStatus.Refused),
            Timeout = SumFor(DohProbeStatus.Timeout),
            RefusedByHttpStatus = refusedByHttp,
        };
    }
}

/// <summary>
/// The latest probe-outcome summary plus the one from the previous cycle, so the
/// Admin UI and read-only API can show a per-category delta between the two most
/// recent probe runs.
/// </summary>
public sealed record DohProbeSummarySnapshot
{
    public DohProbeOutcomeSummary Current { get; init; } = new();

    public DohProbeOutcomeSummary? Prior { get; init; }
}

/// <summary>Persistence port for the two most recent probe-outcome summaries.</summary>
public interface IDohProbeSummaryStore
{
    ValueTask<DohProbeSummarySnapshot?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="current"/> as the latest summary, rolling the previous latest into Prior.</summary>
    ValueTask SaveCurrentAsync(DohProbeOutcomeSummary current, CancellationToken cancellationToken = default);
}
