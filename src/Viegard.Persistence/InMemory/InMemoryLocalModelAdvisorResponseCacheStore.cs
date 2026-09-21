using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryLocalModelAdvisorResponseCacheStore : ILocalModelAdvisorResponseCacheStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LocalModelAdvisorResponseCacheEntry> _entries = new(StringComparer.Ordinal);

    public ValueTask<LocalModelAdvisorResponseCacheEntry?> GetAsync(
        string cacheKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var utc = now.ToUniversalTime();
        lock (_sync)
        {
            if (!_entries.TryGetValue(cacheKey, out var entry) || entry.ExpiresAt <= utc)
            {
                return ValueTask.FromResult<LocalModelAdvisorResponseCacheEntry?>(null);
            }

            return ValueTask.FromResult<LocalModelAdvisorResponseCacheEntry?>(entry);
        }
    }

    public ValueTask SetAsync(
        LocalModelAdvisorResponseCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _entries[entry.CacheKey] = Normalize(entry);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> PruneExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var utc = now.ToUniversalTime();
        lock (_sync)
        {
            var keys = _entries
                .Where(pair => pair.Value.ExpiresAt <= utc)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys)
            {
                _entries.Remove(key);
            }

            return ValueTask.FromResult(keys.Length);
        }
    }

    private static LocalModelAdvisorResponseCacheEntry Normalize(LocalModelAdvisorResponseCacheEntry entry) => entry with
    {
        CreatedAt = entry.CreatedAt.ToUniversalTime(),
        ExpiresAt = entry.ExpiresAt.ToUniversalTime(),
        Reasons = entry.Reasons.ToArray(),
    };
}
