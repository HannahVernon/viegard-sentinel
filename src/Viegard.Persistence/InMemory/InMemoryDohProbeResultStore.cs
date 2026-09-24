using Viegard.Application.Doh;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryDohProbeResultStore : IDohProbeResultStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DohProbeResult> _results = new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<DohProbeResult>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<DohProbeResult>>(
                _results.Values
                    .OrderBy(result => result.Address, StringComparer.Ordinal)
                    .ToList());
        }
    }

    public ValueTask SaveAsync(DohProbeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            _results[result.Address] = result;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> PruneAsync(
        IReadOnlyCollection<string> keepAddresses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keepAddresses);
        var keep = keepAddresses.ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            var stale = _results.Keys.Where(address => !keep.Contains(address)).ToList();
            foreach (var address in stale)
            {
                _results.Remove(address);
            }

            return ValueTask.FromResult(stale.Count);
        }
    }
}
