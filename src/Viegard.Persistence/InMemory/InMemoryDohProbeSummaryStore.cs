using Viegard.Application.Doh;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryDohProbeSummaryStore : IDohProbeSummaryStore
{
    private readonly object _sync = new();
    private DohProbeSummarySnapshot? _snapshot;

    public ValueTask<DohProbeSummarySnapshot?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_snapshot);
        }
    }

    public ValueTask SaveCurrentAsync(DohProbeOutcomeSummary current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_sync)
        {
            _snapshot = new DohProbeSummarySnapshot
            {
                Current = current,
                Prior = _snapshot?.Current,
            };
        }

        return ValueTask.CompletedTask;
    }
}
