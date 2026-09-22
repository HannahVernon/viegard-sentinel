using Microsoft.EntityFrameworkCore;
using Viegard.Application.Burst;

namespace Viegard.Persistence.Postgres.Stores;

/// <summary>
/// Durable (signal, source) sliding-window occurrence store.  Occurrences are
/// pruned to the supplied window on every record, and cooldown is enforced with
/// an atomic upsert so concurrent instances cannot both fire a proposal.
/// </summary>
public sealed class PostgresBurstWindowStore(
    IDbContextFactory<ViegardDbContext> factory) : IBurstWindowStore
{
    public async ValueTask<BurstWindowState> RecordAndCountAsync(
        string signalId,
        string sourceKey,
        Guid eventId,
        DateTimeOffset occurredAt,
        TimeSpan window,
        int maxEventIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var occurredUtc = occurredAt.ToUniversalTime();
        var floor = occurredUtc - window;

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO burst_windows (signal_id, source_key, event_id, occurred_at)
            VALUES ({signalId}, {sourceKey}, {eventId}, {occurredUtc});
            """, cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM burst_windows
            WHERE signal_id = {signalId} AND source_key = {sourceKey} AND occurred_at < {floor};
            """, cancellationToken).ConfigureAwait(false);

        var rows = await db.BurstWindows.AsNoTracking()
            .Where(r => r.SignalId == signalId && r.SourceKey == sourceKey)
            .OrderByDescending(r => r.OccurredAt)
            .Select(r => new { r.EventId, r.OccurredAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var count = rows.Count;
        var windowStart = count == 0 ? occurredUtc : rows.Min(r => r.OccurredAt);
        var eventIds = rows
            .Take(Math.Max(1, maxEventIds))
            .Select(r => r.EventId)
            .ToList();

        return new BurstWindowState(count, eventIds, windowStart, occurredUtc);
    }

    public async ValueTask<bool> TryBeginCooldownAsync(
        string signalId,
        string sourceKey,
        DateTimeOffset firedAt,
        TimeSpan cooldown,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var firedUtc = firedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO burst_cooldowns (signal_id, source_key, last_fired_at)
            VALUES ({signalId}, {sourceKey}, {firedUtc})
            ON CONFLICT (signal_id, source_key) DO UPDATE
                SET last_fired_at = EXCLUDED.last_fired_at
                WHERE burst_cooldowns.last_fired_at + {cooldown} <= EXCLUDED.last_fired_at;
            """, cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }
}
