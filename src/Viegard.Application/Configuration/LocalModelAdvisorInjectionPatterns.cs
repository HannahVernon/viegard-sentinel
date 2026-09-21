using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Viegard.Domain;

namespace Viegard.Application.Configuration;

public enum AdvisorInjectionAction
{
    RecordOnly,
    SkipAdvisor,
}

public enum AdvisorInjectionPatternCategory
{
    InstructionOverride,
    RoleOrSystemImpersonation,
    OutputControlHijack,
    DelimiterOrFenceBreakout,
    EncodedPayload,
    ExfiltrationOrToolAbuse,
}

public sealed record LocalModelAdvisorInjectionPattern
{
    public const int MaxCategoryLength = 64;
    public const int MaxPatternLength = 1024;
    public const int MaxDescriptionLength = 512;
    public const int MaxCreatedByLength = LocalModelAdvisorSettings.MaxUpdatedByLength;

    public Guid Id { get; init; } = ViegardId.New();

    public required string Category { get; init; }

    public required string Pattern { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;
}

public sealed record CompiledAdvisorInjectionPattern(
    string Id,
    string Category,
    Regex Regex);

public sealed record AdvisorInjectionDetectionResult(
    bool Detected,
    IReadOnlyList<string> Categories)
{
    public static AdvisorInjectionDetectionResult None { get; } = new(false, []);
}

public static class LocalModelAdvisorInjectionPatternValidator
{
    public const string CategoryError = "Local-model advisor injection pattern category was not valid.";
    public const string PatternError = "Local-model advisor injection pattern regex must be non-empty, must not exceed 1024 characters, and must compile.";
    public const string DescriptionError = "Local-model advisor injection pattern description must not exceed 512 characters and must not contain control characters other than normal whitespace.";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static bool TryValidate(LocalModelAdvisorInjectionPattern pattern, out string error)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!TryNormalizeCategory(pattern.Category, out _, out error)
            || !TryNormalizePattern(pattern.Pattern, out _, out error)
            || !TryNormalizeDescription(pattern.Description, out _, out error)
            || !TryNormalizeCreatedBy(pattern.CreatedBy, out _, out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeCategory(string? value, out string normalized, out string error)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (!Enum.TryParse<AdvisorInjectionPatternCategory>(normalized, ignoreCase: false, out _)
            || normalized.Length > LocalModelAdvisorInjectionPattern.MaxCategoryLength)
        {
            error = CategoryError;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryNormalizePattern(string? value, out string normalized, out string error)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0
            || normalized.Length > LocalModelAdvisorInjectionPattern.MaxPatternLength
            || normalized.Any(IsDisallowedControl))
        {
            error = PatternError;
            return false;
        }

        try
        {
            _ = Compile(normalized);
        }
        catch (ArgumentException)
        {
            error = PatternError;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeDescription(string? value, out string? normalized, out string error)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            normalized = null;
            error = string.Empty;
            return true;
        }

        if (candidate.Length > LocalModelAdvisorInjectionPattern.MaxDescriptionLength
            || candidate.Any(IsDisallowedControl))
        {
            normalized = null;
            error = DescriptionError;
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
            || normalized.Length > LocalModelAdvisorInjectionPattern.MaxCreatedByLength
            || normalized.Any(char.IsControl))
        {
            error = "Local-model advisor injection pattern author was not valid.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static Regex Compile(string pattern) =>
        new(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline,
            MatchTimeout);

    private static bool IsDisallowedControl(char value) =>
        char.IsControl(value) && value is not '\n' and not '\r' and not '\t';
}

public static class LocalModelAdvisorBaseInjectionPatterns
{
    public static IReadOnlyList<CompiledAdvisorInjectionPattern> All { get; } =
    [
        P("instruction-override-previous-instructions", AdvisorInjectionPatternCategory.InstructionOverride, @"\b(ignore|disregard|forget|override)\s+(all\s+|any\s+)?(previous|prior|above|earlier|preceding)\s+(instructions?|prompts?|directives?|rules?|context)\b"),
        P("instruction-override-do-not-follow", AdvisorInjectionPatternCategory.InstructionOverride, @"\bdo\s+not\s+follow\s+(the\s+)?(previous|prior|above|original|system)\s+(instructions?|prompt|rules?)\b"),
        P("instruction-override-new-instructions", AdvisorInjectionPatternCategory.InstructionOverride, @"\bnew\s+instructions?\s*:"),
        P("instruction-override-from-now-on", AdvisorInjectionPatternCategory.InstructionOverride, @"\bfrom\s+now\s+on\s*,?\s+you\s+(must|will|are|should)\b"),
        P("role-impersonation-you-are-now", AdvisorInjectionPatternCategory.RoleOrSystemImpersonation, @"\byou\s+are\s+now\s+(a|an|the|in)\b"),
        P("role-impersonation-act-pretend", AdvisorInjectionPatternCategory.RoleOrSystemImpersonation, @"\b(act|pretend)\s+(as\s+|that\s+you\s+are\s+)(a\s+|an\s+|the\s+)?(system|developer|admin|root|dan|jailbroken|unrestricted|unfiltered)\b"),
        P("role-impersonation-im-token", AdvisorInjectionPatternCategory.RoleOrSystemImpersonation, @"<\|im_(start|end)\|>"),
        P("role-impersonation-inst-token", AdvisorInjectionPatternCategory.RoleOrSystemImpersonation, @"\[/?INST\]"),
        P("role-impersonation-sys-token", AdvisorInjectionPatternCategory.RoleOrSystemImpersonation, @"<<SYS>>|<</SYS>>"),
        P("role-impersonation-markdown-heading", AdvisorInjectionPatternCategory.RoleOrSystemImpersonation, @"(^|\n)\s*#{2,4}\s*(system|assistant)\s*(:|$|\n)"),
        P("output-control-severity", AdvisorInjectionPatternCategory.OutputControlHijack, @"\b(set|change|force|lower)\s+(the\s+)?severity\s*(to|=|:)\s*(0|low|none|informational)\b"),
        P("output-control-classify", AdvisorInjectionPatternCategory.OutputControlHijack, @"\bclassify\s+(this|it|the\s+(incident|alert|email|message))\s+as\s+(benign|safe|clean|legitimate|not\s+malicious|false\s+positive)\b"),
        P("output-control-mark", AdvisorInjectionPatternCategory.OutputControlHijack, @"\b(mark|flag|label)\s+this\s+(alert|incident|email|message|event)\s+as\s+(safe|benign|clean|legitimate|false\s+positive)\b"),
        P("output-control-zero-confidence", AdvisorInjectionPatternCategory.OutputControlHijack, @"\brespond\s+with\s+(a\s+)?confidence\s+(of\s+|score\s+of\s+)?(0|zero)\b"),
        P("output-control-do-not-escalate", AdvisorInjectionPatternCategory.OutputControlHijack, @"\bdo\s+not\s+(escalate|report|flag|alert\s+on)\s+(this|it)\b"),
        P("output-control-final-output", AdvisorInjectionPatternCategory.OutputControlHijack, @"\byour\s+(final\s+)?(verdict|answer|output|response|classification)\s+(must|should|will)\s+be\b"),
        P("output-control-respond-only", AdvisorInjectionPatternCategory.OutputControlHijack, @"\brespond\s+only\s+with\b"),
        P("delimiter-fence-breakout", AdvisorInjectionPatternCategory.DelimiterOrFenceBreakout, @"(^|\n)\s*`{3,}\s*($|\n)"),
        P("delimiter-end-untrusted-data", AdvisorInjectionPatternCategory.DelimiterOrFenceBreakout, @"\bend\s+of\s+(untrusted|observed|quoted|user)\s+(data|input|content|section|block)\b"),
        P("delimiter-system-tags", AdvisorInjectionPatternCategory.DelimiterOrFenceBreakout, @"</?(system|instructions?|prompt|assistant)\s*>"),
        P("delimiter-system-message", AdvisorInjectionPatternCategory.DelimiterOrFenceBreakout, @"\b(begin|end)\s+(system|assistant)\s+(message|prompt|block)\b"),
        P("encoded-decode-then-obey", AdvisorInjectionPatternCategory.EncodedPayload, @"\bdecode\s+(this|the\s+following|it)\b.{0,60}\b(follow|execute|apply|obey|run)\b"),
        P("encoded-long-base64", AdvisorInjectionPatternCategory.EncodedPayload, @"(?:[A-Za-z0-9+/]{4}){40,}={0,2}"),
        P("encoded-long-hex", AdvisorInjectionPatternCategory.EncodedPayload, @"\b(?:[0-9a-f]{2}){65,}\b"),
        P("exfil-reveal-prompt", AdvisorInjectionPatternCategory.ExfiltrationOrToolAbuse, @"\b(reveal|print|show|display|repeat|output|disclose)\s+(your|the)\s+(system\s+|hidden\s+|initial\s+)?(prompt|instructions)\b"),
        P("exfil-original-instructions", AdvisorInjectionPatternCategory.ExfiltrationOrToolAbuse, @"\bwhat\s+(are|were)\s+your\s+(original\s+|initial\s+)?(instructions|system\s+prompt)\b"),
        P("exfil-send-to-url", AdvisorInjectionPatternCategory.ExfiltrationOrToolAbuse, @"\bsend\s+(the\s+)?(results?|output|data|contents?|prompt|analysis)\s+to\s+https?://"),
        P("tool-abuse-run-command", AdvisorInjectionPatternCategory.ExfiltrationOrToolAbuse, @"\byou\s+(must|should|need\s+to)\s+(run|execute|call|invoke)\s+(the\s+|this\s+)?(command|tool|script|function)\b"),
        P("tool-abuse-pipe-to-shell", AdvisorInjectionPatternCategory.ExfiltrationOrToolAbuse, @"https?://\S{1,200}\s*\|\s*(sh|bash|powershell|iex)\b"),
    ];

    private static CompiledAdvisorInjectionPattern P(string id, AdvisorInjectionPatternCategory category, string pattern) =>
        new(id, category.ToString(), LocalModelAdvisorInjectionPatternValidator.Compile(pattern));
}

public interface ILocalModelAdvisorInjectionPatternStore
{
    ValueTask<IReadOnlyList<LocalModelAdvisorInjectionPattern>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorInjectionPattern?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorInjectionPattern> CreateAsync(
        string category,
        string pattern,
        string? description,
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorInjectionPatternToggleResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorInjectionPatternDeleteResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public enum LocalModelAdvisorInjectionPatternMutationStatus
{
    Updated,
    NotFound,
}

public sealed record LocalModelAdvisorInjectionPatternToggleResult(
    LocalModelAdvisorInjectionPatternMutationStatus Status,
    LocalModelAdvisorInjectionPattern? Before,
    LocalModelAdvisorInjectionPattern? After)
{
    public bool Succeeded => Status == LocalModelAdvisorInjectionPatternMutationStatus.Updated;

    public static LocalModelAdvisorInjectionPatternToggleResult Updated(
        LocalModelAdvisorInjectionPattern before,
        LocalModelAdvisorInjectionPattern after) =>
        new(LocalModelAdvisorInjectionPatternMutationStatus.Updated, before, after);

    public static LocalModelAdvisorInjectionPatternToggleResult NotFound() =>
        new(LocalModelAdvisorInjectionPatternMutationStatus.NotFound, null, null);
}

public sealed record LocalModelAdvisorInjectionPatternDeleteResult(
    LocalModelAdvisorInjectionPatternMutationStatus Status,
    LocalModelAdvisorInjectionPattern? Before)
{
    public bool Succeeded => Status == LocalModelAdvisorInjectionPatternMutationStatus.Updated;

    public static LocalModelAdvisorInjectionPatternDeleteResult Deleted(LocalModelAdvisorInjectionPattern before) =>
        new(LocalModelAdvisorInjectionPatternMutationStatus.Updated, before);

    public static LocalModelAdvisorInjectionPatternDeleteResult NotFound() =>
        new(LocalModelAdvisorInjectionPatternMutationStatus.NotFound, null);
}

public sealed class LocalModelAdvisorInjectionPatternSource(
    ILocalModelAdvisorInjectionPatternStore? patternStore = null,
    ILocalModelAdvisorDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly ILocalModelAdvisorDiagnostics _diagnostics = diagnostics ?? NullLocalModelAdvisorDiagnostics.Instance;
    private IReadOnlyList<CompiledAdvisorInjectionPattern> _snapshot = [];

    public IReadOnlyList<CompiledAdvisorInjectionPattern> Current => Volatile.Read(ref _snapshot);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (patternStore is null)
        {
            Interlocked.Exchange(ref _snapshot, []);
            return;
        }

        try
        {
            var rows = await patternStore.ListAsync(cancellationToken).ConfigureAwait(false);
            var compiled = ImmutableArray.CreateBuilder<CompiledAdvisorInjectionPattern>(rows.Count);
            foreach (var row in rows.Where(p => p.Enabled))
            {
                try
                {
                    if (!LocalModelAdvisorInjectionPatternValidator.TryNormalizeCategory(row.Category, out var category, out _)
                        || !LocalModelAdvisorInjectionPatternValidator.TryNormalizePattern(row.Pattern, out var pattern, out _))
                    {
                        continue;
                    }

                    compiled.Add(new CompiledAdvisorInjectionPattern(row.Id.ToString("N"), category, LocalModelAdvisorInjectionPatternValidator.Compile(pattern)));
                }
                catch (ArgumentException)
                {
                }
            }

            Interlocked.Exchange(ref _snapshot, compiled.ToImmutable());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.RefreshFailed(ex);
        }
    }

    public async Task RunRefreshLoopAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (patternStore is null)
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

public sealed class LocalModelAdvisorInjectionDetector(LocalModelAdvisorInjectionPatternSource? patternSource = null)
{
    public AdvisorInjectionDetectionResult Detect(IEnumerable<string> untrustedObservedData)
    {
        ArgumentNullException.ThrowIfNull(untrustedObservedData);
        var categories = new List<string>(capacity: 4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in untrustedObservedData)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            ScanPatterns(LocalModelAdvisorBaseInjectionPatterns.All, value, categories, seen);
            if (patternSource is not null)
            {
                ScanPatterns(patternSource.Current, value, categories, seen);
            }
        }

        return categories.Count == 0
            ? AdvisorInjectionDetectionResult.None
            : new AdvisorInjectionDetectionResult(true, categories);
    }

    private static void ScanPatterns(
        IReadOnlyList<CompiledAdvisorInjectionPattern> patterns,
        string value,
        List<string> categories,
        HashSet<string> seen)
    {
        foreach (var pattern in patterns)
        {
            if (seen.Contains(pattern.Category))
            {
                continue;
            }

            try
            {
                if (pattern.Regex.IsMatch(value))
                {
                    seen.Add(pattern.Category);
                    categories.Add(pattern.Category);
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
    }
}
