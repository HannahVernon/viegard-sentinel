using Viegard.Application.Configuration;

namespace Viegard.Application.Stores;

public interface IIngestionFilterStore
{
    long CurrentChangeVersion { get; }

    ValueTask<IReadOnlyList<IngestionFilter>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IngestionFilter>> ListForSourceAsync(
        string sourceType,
        CancellationToken cancellationToken = default);

    ValueTask<IngestionFilterSaveResult> SaveMatrixAsync(
        string sourceType,
        IReadOnlyDictionary<string, bool> suppressions,
        IReadOnlySet<string> lockedEventKinds,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask SeedDefaultsIfMissingAsync(DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(long lastSeenVersion, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed record IngestionFilterSaveResult(
    IReadOnlyList<IngestionFilter> Before,
    IReadOnlyList<IngestionFilter> After);
