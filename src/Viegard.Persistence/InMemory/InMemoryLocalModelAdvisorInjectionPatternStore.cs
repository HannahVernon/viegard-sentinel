using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryLocalModelAdvisorInjectionPatternStore : ILocalModelAdvisorInjectionPatternStore
{
    private readonly object _sync = new();
    private readonly List<LocalModelAdvisorInjectionPattern> _patterns = [];

    public ValueTask<IReadOnlyList<LocalModelAdvisorInjectionPattern>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<LocalModelAdvisorInjectionPattern>>(
                _patterns.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToList());
        }
    }

    public ValueTask<LocalModelAdvisorInjectionPattern?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_patterns.FirstOrDefault(p => p.Id == id));
        }
    }

    public ValueTask<LocalModelAdvisorInjectionPattern> CreateAsync(
        string category,
        string pattern,
        string? description,
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var candidate = NormalizeNew(category, pattern, description, createdBy, createdAt);
        lock (_sync)
        {
            _patterns.Add(candidate);
        }

        return ValueTask.FromResult(candidate);
    }

    public ValueTask<LocalModelAdvisorInjectionPatternToggleResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var index = _patterns.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                return ValueTask.FromResult(LocalModelAdvisorInjectionPatternToggleResult.NotFound());
            }

            var before = _patterns[index];
            var after = before with { Enabled = enabled };
            _patterns[index] = after;
            return ValueTask.FromResult(LocalModelAdvisorInjectionPatternToggleResult.Updated(before, after));
        }
    }

    public ValueTask<LocalModelAdvisorInjectionPatternDeleteResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var index = _patterns.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                return ValueTask.FromResult(LocalModelAdvisorInjectionPatternDeleteResult.NotFound());
            }

            var before = _patterns[index];
            _patterns.RemoveAt(index);
            return ValueTask.FromResult(LocalModelAdvisorInjectionPatternDeleteResult.Deleted(before));
        }
    }

    private static LocalModelAdvisorInjectionPattern NormalizeNew(
        string category,
        string pattern,
        string? description,
        string createdBy,
        DateTimeOffset createdAt)
    {
        if (!LocalModelAdvisorInjectionPatternValidator.TryNormalizeCategory(category, out var normalizedCategory, out var error)
            || !LocalModelAdvisorInjectionPatternValidator.TryNormalizePattern(pattern, out var normalizedPattern, out error)
            || !LocalModelAdvisorInjectionPatternValidator.TryNormalizeDescription(description, out var normalizedDescription, out error)
            || !LocalModelAdvisorInjectionPatternValidator.TryNormalizeCreatedBy(createdBy, out var normalizedCreatedBy, out error))
        {
            throw new InvalidOperationException(error);
        }

        return new LocalModelAdvisorInjectionPattern
        {
            Category = normalizedCategory,
            Pattern = normalizedPattern,
            Description = normalizedDescription,
            Enabled = true,
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = normalizedCreatedBy,
        };
    }
}
