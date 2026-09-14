using Viegard.Application.Retention;

namespace Viegard.Persistence.InMemory;

/// <summary>Development-only retention store.  In-memory data is process-lifetime only.</summary>
public sealed class InMemoryRetentionStore : IRetentionStore
{
    public ValueTask<long> PurgeAsync(
        RetentionTarget target,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0L);
}
