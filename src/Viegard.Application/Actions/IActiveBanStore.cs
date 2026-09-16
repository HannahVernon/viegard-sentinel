using Viegard.Domain.Actions;

namespace Viegard.Application.Actions;

public interface IActiveBanStore
{
    ValueTask<ActiveBan> UpsertByIpAsync(ActiveBan activeBan, CancellationToken cancellationToken = default);

    ValueTask<bool> RemoveByIpAsync(string ip, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ActiveBan>> ListUnexpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    ValueTask<long> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    ValueTask<ActiveBan?> GetByIpAsync(string ip, CancellationToken cancellationToken = default);
}
