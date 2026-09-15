namespace Viegard.Application.Retention;

/// <summary>Persistence port for scheduled retention purges.</summary>
public interface IRetentionStore
{
    ValueTask<long> PurgeAsync(
        RetentionTarget target,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken = default);
}
