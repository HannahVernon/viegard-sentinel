using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryLocalModelAdvisorPromptTemplateStore : ILocalModelAdvisorPromptTemplateStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<LocalModelAdvisorPromptTemplateRevision>> _revisions = new(StringComparer.Ordinal);

    public ValueTask<LocalModelAdvisorPromptTemplateRevision?> GetActiveAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        if (!LocalModelAdvisorPromptTemplateValidator.TryNormalizeTemplateId(templateId, out var normalized, out _))
        {
            return ValueTask.FromResult<LocalModelAdvisorPromptTemplateRevision?>(null);
        }

        lock (_sync)
        {
            return ValueTask.FromResult(_revisions.TryGetValue(normalized, out var revisions)
                ? revisions.FirstOrDefault(r => r.IsActive)
                : null);
        }
    }

    public ValueTask<IReadOnlyList<LocalModelAdvisorPromptTemplateRevision>> ListAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        if (!LocalModelAdvisorPromptTemplateValidator.TryNormalizeTemplateId(templateId, out var normalized, out _))
        {
            return ValueTask.FromResult<IReadOnlyList<LocalModelAdvisorPromptTemplateRevision>>([]);
        }

        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<LocalModelAdvisorPromptTemplateRevision>>(
                _revisions.TryGetValue(normalized, out var revisions)
                    ? revisions.OrderByDescending(r => r.Revision).ToList()
                    : []);
        }
    }

    public ValueTask<LocalModelAdvisorPromptTemplateRevision?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_revisions.Values.SelectMany(r => r).FirstOrDefault(r => r.Id == id));
        }
    }

    public ValueTask<LocalModelAdvisorPromptTemplateRevision> CreateRevisionAsync(
        string templateId,
        string systemInstructions,
        string applicationInstructions,
        string? note,
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var revision = NormalizeNewRevision(templateId, systemInstructions, applicationInstructions, note, createdBy, createdAt);
        lock (_sync)
        {
            if (!_revisions.TryGetValue(revision.TemplateId, out var revisions))
            {
                revisions = [];
                _revisions[revision.TemplateId] = revisions;
            }

            var next = revisions.Count == 0 ? 1 : revisions.Max(r => r.Revision) + 1;
            var active = revision with { Revision = next, IsActive = true };
            for (var i = 0; i < revisions.Count; i++)
            {
                revisions[i] = revisions[i] with { IsActive = false };
            }

            revisions.Add(active);
            return ValueTask.FromResult(active);
        }
    }

    public ValueTask<LocalModelAdvisorPromptTemplateActivateResult> ActivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            foreach (var revisions in _revisions.Values)
            {
                var index = revisions.FindIndex(r => r.Id == id);
                if (index < 0)
                {
                    continue;
                }

                var target = revisions[index];
                var before = revisions.FirstOrDefault(r => r.IsActive);
                for (var i = 0; i < revisions.Count; i++)
                {
                    revisions[i] = revisions[i] with { IsActive = revisions[i].Id == id };
                }

                return ValueTask.FromResult(LocalModelAdvisorPromptTemplateActivateResult.Activated(
                    before,
                    target with { IsActive = true }));
            }

            return ValueTask.FromResult(LocalModelAdvisorPromptTemplateActivateResult.NotFound());
        }
    }

    private static LocalModelAdvisorPromptTemplateRevision NormalizeNewRevision(
        string templateId,
        string systemInstructions,
        string applicationInstructions,
        string? note,
        string createdBy,
        DateTimeOffset createdAt)
    {
        if (!LocalModelAdvisorPromptTemplateValidator.TryNormalizeTemplateId(templateId, out var normalizedTemplateId, out var error)
            || !LocalModelAdvisorPromptTemplateValidator.TryNormalizeInstructions(systemInstructions, out var normalizedSystem, out error)
            || !LocalModelAdvisorPromptTemplateValidator.TryNormalizeInstructions(applicationInstructions, out var normalizedApplication, out error)
            || !LocalModelAdvisorPromptTemplateValidator.TryNormalizeNote(note, out var normalizedNote, out error)
            || !LocalModelAdvisorPromptTemplateValidator.TryNormalizeCreatedBy(createdBy, out var normalizedCreatedBy, out error)
            || !LocalModelAdvisorPromptTemplateValidator.TryValidate(normalizedTemplateId, normalizedSystem, normalizedApplication, normalizedNote, out error))
        {
            throw new InvalidOperationException(error);
        }

        return new LocalModelAdvisorPromptTemplateRevision
        {
            TemplateId = normalizedTemplateId,
            SystemInstructions = normalizedSystem,
            ApplicationInstructions = normalizedApplication,
            Note = normalizedNote,
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = normalizedCreatedBy,
        };
    }
}
