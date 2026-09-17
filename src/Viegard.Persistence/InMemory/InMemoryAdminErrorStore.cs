using Viegard.Application.Stores;
using Viegard.Domain.Admin;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryAdminErrorStore : IAdminErrorStore
{
    private readonly object _sync = new();
    private readonly List<AdminError> _errors = [];

    public ValueTask AddAsync(AdminError error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_sync)
        {
            _errors.Add(error);
            if (_errors.Count > AdminError.KeepNewest)
            {
                var excess = _errors
                    .OrderByDescending(e => e.OccurredAt)
                    .Skip(AdminError.KeepNewest)
                    .ToList();
                foreach (var stale in excess)
                {
                    _errors.Remove(stale);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AdminError>> ListRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<AdminError> page = _errors
                .OrderByDescending(e => e.OccurredAt)
                .Take(Math.Max(1, take))
                .ToList();
            return ValueTask.FromResult(page);
        }
    }

    public ValueTask<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            long removed = _errors.Count;
            _errors.Clear();
            return ValueTask.FromResult(removed);
        }
    }
}
