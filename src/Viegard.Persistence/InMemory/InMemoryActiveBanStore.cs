using Viegard.Application.Actions;
using Viegard.Domain.Actions;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryActiveBanStore : IActiveBanStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ActiveBan> _bans = new(StringComparer.Ordinal);

    public ValueTask<ActiveBan> UpsertByIpAsync(ActiveBan activeBan, CancellationToken cancellationToken = default)
    {
        var normalized = ActiveBan.NormalizeForSave(activeBan);
        lock (_sync)
        {
            _bans[normalized.Ip] = normalized;
        }

        return ValueTask.FromResult(normalized);
    }

    public ValueTask<bool> RemoveByIpAsync(string ip, CancellationToken cancellationToken = default)
    {
        var normalizedIp = ActiveBan.CanonicalizeIp(ip);
        lock (_sync)
        {
            return ValueTask.FromResult(_bans.Remove(normalizedIp));
        }
    }

    public ValueTask<IReadOnlyList<ActiveBan>> ListUnexpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var cutoff = now.ToUniversalTime();
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<ActiveBan>>(
                _bans.Values
                    .Where(ban => ban.ExpiresAt > cutoff)
                    .OrderBy(ban => ban.Ip, StringComparer.Ordinal)
                    .ToList());
        }
    }

    public ValueTask<long> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now.ToUniversalTime();
        lock (_sync)
        {
            var expired = _bans
                .Where(pair => pair.Value.ExpiresAt <= cutoff)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var ip in expired)
            {
                _bans.Remove(ip);
            }

            return ValueTask.FromResult((long)expired.Count);
        }
    }

    public ValueTask<ActiveBan?> GetByIpAsync(string ip, CancellationToken cancellationToken = default)
    {
        var normalizedIp = ActiveBan.CanonicalizeIp(ip);
        lock (_sync)
        {
            return ValueTask.FromResult(_bans.GetValueOrDefault(normalizedIp));
        }
    }
}
