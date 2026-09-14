using Viegard.Domain.Configuration;

namespace Viegard.Application.Stores;

public interface ICustomSignatureStore
{
    long CurrentChangeVersion { get; }

    ValueTask<IReadOnlyList<CustomSignature>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<CustomSignature?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<CustomSignature> UpsertAsync(CustomSignature signature, CancellationToken cancellationToken = default);

    ValueTask<CustomSignature?> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(long lastSeenVersion, TimeSpan timeout, CancellationToken cancellationToken = default);
}
