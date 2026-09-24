using Viegard.Application.Doh;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryDohDesiredAddressStore : IDohDesiredAddressStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DohDesiredAddress> _addresses = new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<DohDesiredAddress>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<DohDesiredAddress>>(
                _addresses.Values
                    .OrderBy(address => address.Address, StringComparer.Ordinal)
                    .ToList());
        }
    }

    public ValueTask<DohDesiredAddressRefreshResult> ReplaceSnapshotAsync(
        IReadOnlyCollection<string> addresses,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var utcObservedAt = observedAt.ToUniversalTime();
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            if (!DohAddressValidator.TryNormalizeAddress(address, out var canonical, out var error))
            {
                throw new InvalidOperationException(error);
            }

            normalized.Add(canonical);
        }

        lock (_sync)
        {
            var added = 0;
            var updated = 0;
            foreach (var address in normalized)
            {
                if (_addresses.TryGetValue(address, out var existing))
                {
                    _addresses[address] = existing with { LastSeenAt = utcObservedAt };
                    updated++;
                }
                else
                {
                    _addresses[address] = new DohDesiredAddress
                    {
                        Address = address,
                        FirstSeenAt = utcObservedAt,
                        LastSeenAt = utcObservedAt,
                    };
                    added++;
                }
            }

            var removed = 0;
            foreach (var address in _addresses.Keys.Except(normalized, StringComparer.Ordinal).ToList())
            {
                _addresses.Remove(address);
                removed++;
            }

            return ValueTask.FromResult(new DohDesiredAddressRefreshResult(
                added,
                updated,
                removed,
                _addresses.Count));
        }
    }
}
