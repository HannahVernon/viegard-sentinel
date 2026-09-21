using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Viegard.Application.Classifiers;
using Viegard.Application.Inference.Prompts;
using Viegard.Domain;

namespace Viegard.Application.Configuration;

public sealed record LocalModelAdvisorPromptTemplateRevision
{
    public const int MaxTemplateIdLength = 64;
    public const int MaxInstructionsLength = 8000;
    public const int MaxNoteLength = 256;
    public const int MaxCreatedByLength = LocalModelAdvisorSettings.MaxUpdatedByLength;

    public Guid Id { get; init; } = ViegardId.New();

    public required string TemplateId { get; init; }

    public int Revision { get; init; }

    public required string SystemInstructions { get; init; }

    public required string ApplicationInstructions { get; init; }

    public bool IsActive { get; init; }

    public string? Note { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;

    public PromptTemplate ToPromptTemplate() => new()
    {
        TemplateId = TemplateId,
        Version = $"{AdvisoryIncidentClassifier.PromptTemplateVersion}#r{Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        SystemInstructions = SystemInstructions,
        ApplicationInstructions = ApplicationInstructions,
    };
}

public static partial class LocalModelAdvisorPromptTemplateValidator
{
    public const string TemplateIdError = "Local-model advisor prompt template id must be non-empty, must not exceed 64 characters, and must not contain control characters.";
    public const string InstructionsError = "Local-model advisor prompt instructions must be non-empty, must not exceed 8000 characters, and must not contain control characters other than tab or line breaks.";
    public const string OutputSchemaError = "Local-model advisor prompt instructions must include {output_schema}.";
    public const string NoteError = "Local-model advisor prompt template note must not exceed 256 characters and must not contain control characters other than normal whitespace.";

    public static IReadOnlySet<string> AllowedPlaceholders => AdvisoryIncidentClassifier.TrustedPromptVariableNames;

    public static bool TryValidate(
        string templateId,
        string systemInstructions,
        string applicationInstructions,
        string? note,
        out string error)
    {
        if (!TryNormalizeTemplateId(templateId, out _, out error)
            || !TryNormalizeInstructions(systemInstructions, out _, out error)
            || !TryNormalizeInstructions(applicationInstructions, out _, out error)
            || !TryNormalizeNote(note, out _, out error))
        {
            return false;
        }

        var hasOutputSchema = false;
        foreach (Match match in PlaceholderPattern().Matches(systemInstructions + applicationInstructions))
        {
            var name = match.Groups["name"].Value;
            if (!AllowedPlaceholders.Contains(name))
            {
                error = $"Local-model advisor prompt placeholder {{{name}}} is not allowed.";
                return false;
            }

            hasOutputSchema |= string.Equals(name, "output_schema", StringComparison.Ordinal);
        }

        if (!hasOutputSchema)
        {
            error = OutputSchemaError;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidate(LocalModelAdvisorPromptTemplateRevision revision, out string error)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (!TryValidate(
                revision.TemplateId,
                revision.SystemInstructions,
                revision.ApplicationInstructions,
                revision.Note,
                out error))
        {
            return false;
        }

        if (revision.Revision < 0)
        {
            error = "Local-model advisor prompt template revision must not be negative.";
            return false;
        }

        if (!TryNormalizeCreatedBy(revision.CreatedBy, out _, out error))
        {
            return false;
        }

        return true;
    }

    public static bool TryNormalizeTemplateId(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0
            || candidate.Length > LocalModelAdvisorPromptTemplateRevision.MaxTemplateIdLength
            || candidate.Any(char.IsControl))
        {
            error = TemplateIdError;
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeInstructions(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0
            || candidate.Length > LocalModelAdvisorPromptTemplateRevision.MaxInstructionsLength
            || candidate.Any(IsDisallowedControl))
        {
            error = InstructionsError;
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeNote(string? value, out string? normalized, out string error)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            normalized = null;
            error = string.Empty;
            return true;
        }

        if (candidate.Length > LocalModelAdvisorPromptTemplateRevision.MaxNoteLength
            || candidate.Any(IsDisallowedControl))
        {
            normalized = null;
            error = NoteError;
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeCreatedBy(string? value, out string normalized, out string error)
    {
        normalized = LocalModelAdvisorSettingsValidator.NormalizeUpdatedBy(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > LocalModelAdvisorPromptTemplateRevision.MaxCreatedByLength
            || normalized.Any(char.IsControl))
        {
            error = "Local-model advisor prompt template author was not valid.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsDisallowedControl(char value) =>
        char.IsControl(value) && value is not '\n' and not '\r' and not '\t';

    [GeneratedRegex("\\{(?<name>[A-Za-z0-9_-]+)\\}")]
    private static partial Regex PlaceholderPattern();
}

public interface ILocalModelAdvisorPromptTemplateStore
{
    ValueTask<LocalModelAdvisorPromptTemplateRevision?> GetActiveAsync(
        string templateId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<LocalModelAdvisorPromptTemplateRevision>> ListAsync(
        string templateId,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorPromptTemplateRevision?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorPromptTemplateRevision> CreateRevisionAsync(
        string templateId,
        string systemInstructions,
        string applicationInstructions,
        string? note,
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorPromptTemplateActivateResult> ActivateAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public enum LocalModelAdvisorPromptTemplateActivateStatus
{
    Activated,
    NotFound,
}

public sealed record LocalModelAdvisorPromptTemplateActivateResult(
    LocalModelAdvisorPromptTemplateActivateStatus Status,
    LocalModelAdvisorPromptTemplateRevision? Before,
    LocalModelAdvisorPromptTemplateRevision? After)
{
    public bool Succeeded => Status == LocalModelAdvisorPromptTemplateActivateStatus.Activated;

    public static LocalModelAdvisorPromptTemplateActivateResult Activated(
        LocalModelAdvisorPromptTemplateRevision? before,
        LocalModelAdvisorPromptTemplateRevision after) =>
        new(LocalModelAdvisorPromptTemplateActivateStatus.Activated, before, after);

    public static LocalModelAdvisorPromptTemplateActivateResult NotFound() =>
        new(LocalModelAdvisorPromptTemplateActivateStatus.NotFound, null, null);
}

public sealed class LocalModelAdvisorPromptTemplateSource(
    ILocalModelAdvisorPromptTemplateStore? promptTemplateStore = null,
    ILocalModelAdvisorDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly ILocalModelAdvisorDiagnostics _diagnostics = diagnostics ?? NullLocalModelAdvisorDiagnostics.Instance;
    private PromptTemplate _snapshot = LocalModelAdvisorPrompt.Template;
    private LocalModelAdvisorPromptTemplateRevision? _activeRevision;

    public PromptTemplate Current => Volatile.Read(ref _snapshot);

    public LocalModelAdvisorPromptTemplateRevision? ActiveRevision => Volatile.Read(ref _activeRevision);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (promptTemplateStore is null)
        {
            Interlocked.Exchange(ref _snapshot, LocalModelAdvisorPrompt.Template);
            Interlocked.Exchange(ref _activeRevision, null);
            return;
        }

        try
        {
            var active = await promptTemplateStore
                .GetActiveAsync(AdvisoryIncidentClassifier.PromptTemplateId, cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _activeRevision, active);
            Interlocked.Exchange(ref _snapshot, active?.ToPromptTemplate() ?? LocalModelAdvisorPrompt.Template);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.RefreshFailed(ex);
        }
    }

    public async Task RunRefreshLoopAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (promptTemplateStore is null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
