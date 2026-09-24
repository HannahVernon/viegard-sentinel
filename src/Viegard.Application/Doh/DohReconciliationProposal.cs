namespace Viegard.Application.Doh;

/// <summary>
/// The most recent DoH reconciliation outcome, persisted so the Admin UI and
/// read-only API (which run in a separate process from the reconciliation
/// worker) can show what the last cycle changed or, in propose-only mode, what
/// it would change.  In propose-only mode the per-router lists carry the
/// proposed additions, removals, and comment updates; in apply mode they carry
/// what was actually applied.
/// </summary>
public sealed record DohReconciliationProposal
{
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>True when the cycle was propose-only (global dry-run or Apply-to-routers off).</summary>
    public bool DryRun { get; init; }

    public string AddressListName { get; init; } = string.Empty;

    /// <summary>Number of curated/confirmed candidate addresses the cycle reconciled toward.</summary>
    public int DesiredCount { get; init; }

    public IReadOnlyList<DohRouterProposal> Routers { get; init; } = [];

    public int TotalAdd => Routers.Sum(router => router.ToAdd.Count);

    public int TotalRemove => Routers.Sum(router => router.ToRemove.Count);

    public int TotalCommentUpdate => Routers.Sum(router => router.CommentUpdate.Count);

    public bool HasChanges => TotalAdd > 0 || TotalRemove > 0 || TotalCommentUpdate > 0;
}

/// <summary>Per-router slice of a <see cref="DohReconciliationProposal"/>.</summary>
public sealed record DohRouterProposal
{
    public string RouterName { get; init; } = string.Empty;

    /// <summary>True when the router could not be reconciled this cycle (see <see cref="Detail"/>).</summary>
    public bool Skipped { get; init; }

    public string? Detail { get; init; }

    public IReadOnlyList<string> ToAdd { get; init; } = [];

    public IReadOnlyList<string> ToRemove { get; init; } = [];

    public IReadOnlyList<string> CommentUpdate { get; init; } = [];
}

/// <summary>Persistence port for the latest DoH reconciliation proposal snapshot.</summary>
public interface IDohReconciliationProposalStore
{
    ValueTask<DohReconciliationProposal?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(DohReconciliationProposal proposal, CancellationToken cancellationToken = default);
}
