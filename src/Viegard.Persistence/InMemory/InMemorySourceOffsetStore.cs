using System.Collections.Concurrent;
using Viegard.Application.Stores;

namespace Viegard.Persistence.InMemory;

/// <summary>
/// Development-only offset store.  NOT durable: offsets vanish on restart,
/// so sources re-baseline.  A durable implementation arrives with the
/// database decision (D-0004).
/// </summary>
public sealed class InMemorySourceOffsetStore : ISourceOffsetStore
{
    private readonly ConcurrentDictionary<(string SourceId, string Key), string> _offsets = new();

    public ValueTask<string?> GetAsync(string sourceId, string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_offsets.TryGetValue((sourceId, key), out var value) ? value : null);

    public ValueTask SetAsync(string sourceId, string key, string value, CancellationToken cancellationToken = default)
    {
        _offsets[(sourceId, key)] = value;
        return ValueTask.CompletedTask;
    }
}
