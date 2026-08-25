namespace Viegard.Domain;

/// <summary>
/// The single place Viegard mints entity identifiers (D-0030).  Version 7
/// UUIDs carry a millisecond timestamp in their high-order bytes, so new
/// ids land on the rightmost PostgreSQL b-tree leaves: fewer page splits,
/// less full-page-write WAL, and a hot working set that stays cached.  At
/// least 62 bits of randomness remain, so ids stay unguessable.  Ordering
/// is approximate only (per-generator clocks, no intra-millisecond
/// guarantee); event ordering semantics always come from the explicit
/// timestamp fields, never from id comparison.
/// </summary>
public static class ViegardId
{
    public static Guid New() => Guid.CreateVersion7();
}
