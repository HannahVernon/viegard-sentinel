using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresLocalModelAdvisorPromptTemplateStore(
    IDbContextFactory<ViegardDbContext> factory)
    : ILocalModelAdvisorPromptTemplateStore
{
    public async ValueTask<LocalModelAdvisorPromptTemplateRevision?> GetActiveAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        if (!LocalModelAdvisorPromptTemplateValidator.TryNormalizeTemplateId(templateId, out var normalized, out _))
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.LocalModelAdvisorPromptTemplates.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TemplateId == normalized && r.IsActive, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }

    public async ValueTask<IReadOnlyList<LocalModelAdvisorPromptTemplateRevision>> ListAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        if (!LocalModelAdvisorPromptTemplateValidator.TryNormalizeTemplateId(templateId, out var normalized, out _))
        {
            return [];
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.LocalModelAdvisorPromptTemplates.AsNoTracking()
            .Where(r => r.TemplateId == normalized)
            .OrderByDescending(r => r.Revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async ValueTask<LocalModelAdvisorPromptTemplateRevision?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.LocalModelAdvisorPromptTemplates.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                .ConfigureAwait(false))
            ?.ToDomain();
    }

    public async ValueTask<LocalModelAdvisorPromptTemplateRevision> CreateRevisionAsync(
        string templateId,
        string systemInstructions,
        string applicationInstructions,
        string? note,
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var candidate = NormalizeNewRevision(templateId, systemInstructions, applicationInstructions, note, createdBy, createdAt);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE local_model_advisor_prompt_templates IN EXCLUSIVE MODE", cancellationToken).ConfigureAwait(false);
        var nextRevision = await db.LocalModelAdvisorPromptTemplates
            .Where(r => r.TemplateId == candidate.TemplateId)
            .Select(r => (int?)r.Revision)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) is { } max
                ? max + 1
                : 1;

        await db.LocalModelAdvisorPromptTemplates
            .Where(r => r.TemplateId == candidate.TemplateId && r.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.IsActive, false), cancellationToken)
            .ConfigureAwait(false);

        var active = candidate with { Revision = nextRevision, IsActive = true };
        db.LocalModelAdvisorPromptTemplates.Add(active.ToRow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async ValueTask<LocalModelAdvisorPromptTemplateActivateResult> ActivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE local_model_advisor_prompt_templates IN EXCLUSIVE MODE", cancellationToken).ConfigureAwait(false);
        var target = await db.LocalModelAdvisorPromptTemplates.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return LocalModelAdvisorPromptTemplateActivateResult.NotFound();
        }

        var before = await db.LocalModelAdvisorPromptTemplates.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TemplateId == target.TemplateId && r.IsActive, cancellationToken)
            .ConfigureAwait(false);

        await db.LocalModelAdvisorPromptTemplates
            .Where(r => r.TemplateId == target.TemplateId && r.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.IsActive, false), cancellationToken)
            .ConfigureAwait(false);
        await db.LocalModelAdvisorPromptTemplates
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.IsActive, true), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return LocalModelAdvisorPromptTemplateActivateResult.Activated(before?.ToDomain(), target.ToDomain() with { IsActive = true });
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
