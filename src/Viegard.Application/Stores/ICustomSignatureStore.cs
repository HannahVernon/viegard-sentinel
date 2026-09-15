using Viegard.Domain.Configuration;

namespace Viegard.Application.Stores;

public interface ICustomSignatureStore
{
    long CurrentChangeVersion { get; }

    ValueTask<IReadOnlyList<CustomSignature>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<KeysetPage<CustomSignature>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        SignatureListFilter? filter = null,
        ListSort<SignatureSortColumn>? sort = null,
        CancellationToken cancellationToken = default);

    ValueTask<Guid?> GetPageCursorAsync(
        int pageNumber,
        int pageSize,
        SignatureListFilter? filter = null,
        ListSort<SignatureSortColumn>? sort = null,
        CancellationToken cancellationToken = default);

    ValueTask<CustomSignature?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<CustomSignature> UpsertAsync(CustomSignature signature, CancellationToken cancellationToken = default);

    ValueTask<CustomSignature?> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(long lastSeenVersion, TimeSpan timeout, CancellationToken cancellationToken = default);
}
