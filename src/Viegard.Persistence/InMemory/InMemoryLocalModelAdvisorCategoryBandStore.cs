using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryLocalModelAdvisorCategoryBandStore : ILocalModelAdvisorCategoryBandStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LocalModelAdvisorCategoryBand> _bands = new(StringComparer.Ordinal);

    public ValueTask<LocalModelAdvisorCategoryBand?> GetAsync(
        string category,
        CancellationToken cancellationToken = default)
    {
        if (!LocalModelAdvisorCategoryBandValidator.TryNormalizeCategory(category, out var normalized, out _))
        {
            return ValueTask.FromResult<LocalModelAdvisorCategoryBand?>(null);
        }

        lock (_sync)
        {
            return ValueTask.FromResult(
                _bands.TryGetValue(normalized, out var band) ? band : null);
        }
    }

    public ValueTask<IReadOnlyList<LocalModelAdvisorCategoryBand>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<LocalModelAdvisorCategoryBand>>(
                _bands.Values.OrderBy(band => band.Category, StringComparer.Ordinal).ToList());
        }
    }

    public ValueTask<LocalModelAdvisorCategoryBandSaveResult> UpsertAsync(
        LocalModelAdvisorCategoryBand band,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!LocalModelAdvisorCategoryBandValidator.TryValidate(band, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalized = Normalize(band);
        var normalizedUpdatedBy = LocalModelAdvisorCategoryBandValidator.NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();

        lock (_sync)
        {
            if (!_bands.TryGetValue(normalized.Category, out var existing))
            {
                if (expectedVersion != 0)
                {
                    return ValueTask.FromResult(LocalModelAdvisorCategoryBandSaveResult.Conflict(null));
                }

                var created = normalized with
                {
                    Version = 1,
                    UpdatedAt = utcUpdatedAt,
                    UpdatedBy = normalizedUpdatedBy,
                };
                _bands[created.Category] = created;
                return ValueTask.FromResult(LocalModelAdvisorCategoryBandSaveResult.Saved(created));
            }

            if (existing.Version != expectedVersion)
            {
                return ValueTask.FromResult(LocalModelAdvisorCategoryBandSaveResult.Conflict(existing));
            }

            var updated = normalized with
            {
                Version = existing.Version + 1,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            };
            _bands[updated.Category] = updated;
            return ValueTask.FromResult(LocalModelAdvisorCategoryBandSaveResult.Saved(updated));
        }
    }

    public ValueTask<LocalModelAdvisorCategoryBandDeleteResult> DeleteAsync(
        string category,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!LocalModelAdvisorCategoryBandValidator.TryNormalizeCategory(category, out var normalized, out _))
        {
            return ValueTask.FromResult(LocalModelAdvisorCategoryBandDeleteResult.NotFound());
        }

        lock (_sync)
        {
            if (!_bands.TryGetValue(normalized, out var existing))
            {
                return ValueTask.FromResult(LocalModelAdvisorCategoryBandDeleteResult.NotFound());
            }

            if (existing.Version != expectedVersion)
            {
                return ValueTask.FromResult(LocalModelAdvisorCategoryBandDeleteResult.Conflict(existing));
            }

            _bands.Remove(normalized);
            return ValueTask.FromResult(LocalModelAdvisorCategoryBandDeleteResult.Deleted(existing));
        }
    }

    private static LocalModelAdvisorCategoryBand Normalize(LocalModelAdvisorCategoryBand band)
    {
        _ = LocalModelAdvisorCategoryBandValidator.TryNormalizeCategory(band.Category, out var category, out var categoryError)
            ? true
            : throw new InvalidOperationException(categoryError);
        return band with { Category = category };
    }
}
