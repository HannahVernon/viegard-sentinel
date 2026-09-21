using System.Collections.Immutable;

namespace Viegard.Application.Configuration;

public sealed record LocalModelAdvisorCategoryBand
{
    public const int MaxCategoryLength = 64;
    public const int MaxUpdatedByLength = LocalModelAdvisorSettings.MaxUpdatedByLength;
    public static readonly IReadOnlyList<string> KnownCategories =
    [
        "credential-attack",
        "path-traversal",
        "vulnerability-scanner",
        "sql-injection",
        "command-injection",
        "reconnaissance",
        "suspicious-activity",
    ];

    public required string Category { get; init; }

    public bool? Enabled { get; init; }

    public double? InvokeConfidenceMin { get; init; }

    public double? InvokeConfidenceMax { get; init; }

    public int? MaxSeverityDelta { get; init; }

    public double? MaxConfidenceDelta { get; init; }

    public int Version { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;
}

public static class LocalModelAdvisorCategoryBandValidator
{
    public const string CategoryError = "Local-model advisor category must be non-empty, must not exceed 64 characters, and must not contain control characters.";

    public static bool TryValidate(LocalModelAdvisorCategoryBand band, out string error)
    {
        ArgumentNullException.ThrowIfNull(band);

        if (!TryNormalizeCategory(band.Category, out _, out error))
        {
            return false;
        }

        if (!TryValidateNullableFields(band, out error))
        {
            return false;
        }

        if (band.InvokeConfidenceMin is { } min
            && band.InvokeConfidenceMax is { } max
            && min > max)
        {
            error = LocalModelAdvisorSettingsValidator.ConfidenceBandError;
            return false;
        }

        if (band.Version < 0)
        {
            error = "Local-model advisor category override version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidateEffective(
        LocalModelAdvisorCategoryBand band,
        LocalModelAdvisorValues inherited,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        if (!TryValidate(band, out error))
        {
            return false;
        }

        var min = band.InvokeConfidenceMin ?? inherited.InvokeConfidenceMin;
        var max = band.InvokeConfidenceMax ?? inherited.InvokeConfidenceMax;
        if (min > max)
        {
            error = LocalModelAdvisorSettingsValidator.ConfidenceBandError;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeCategory(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = CategoryError;
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length == 0
            || candidate.Length > LocalModelAdvisorCategoryBand.MaxCategoryLength
            || candidate.Any(char.IsControl))
        {
            error = CategoryError;
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy) =>
        LocalModelAdvisorSettingsValidator.NormalizeUpdatedBy(updatedBy);

    private static bool TryValidateNullableFields(LocalModelAdvisorCategoryBand band, out string error)
    {
        if (band.InvokeConfidenceMin is { } min
            && (!double.IsFinite(min) || min is < 0.0 or > 1.0))
        {
            error = LocalModelAdvisorSettingsValidator.ConfidenceBandError;
            return false;
        }

        if (band.InvokeConfidenceMax is { } max
            && (!double.IsFinite(max) || max is < 0.0 or > 1.0))
        {
            error = LocalModelAdvisorSettingsValidator.ConfidenceBandError;
            return false;
        }

        if (band.MaxSeverityDelta is < 0 or > 10)
        {
            error = LocalModelAdvisorSettingsValidator.MaxSeverityDeltaError;
            return false;
        }

        if (band.MaxConfidenceDelta is { } confidenceDelta
            && (!double.IsFinite(confidenceDelta) || confidenceDelta is < 0.0 or > 1.0))
        {
            error = LocalModelAdvisorSettingsValidator.MaxConfidenceDeltaError;
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public interface ILocalModelAdvisorCategoryBandStore
{
    ValueTask<LocalModelAdvisorCategoryBand?> GetAsync(string category, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<LocalModelAdvisorCategoryBand>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorCategoryBandSaveResult> UpsertAsync(
        LocalModelAdvisorCategoryBand band,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorCategoryBandDeleteResult> DeleteAsync(
        string category,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}

public enum LocalModelAdvisorCategoryBandSaveStatus
{
    Saved,
    Conflict,
}

public sealed record LocalModelAdvisorCategoryBandSaveResult(
    LocalModelAdvisorCategoryBandSaveStatus Status,
    LocalModelAdvisorCategoryBand? Band)
{
    public bool Succeeded => Status == LocalModelAdvisorCategoryBandSaveStatus.Saved;

    public static LocalModelAdvisorCategoryBandSaveResult Saved(LocalModelAdvisorCategoryBand band) =>
        new(LocalModelAdvisorCategoryBandSaveStatus.Saved, band);

    public static LocalModelAdvisorCategoryBandSaveResult Conflict(LocalModelAdvisorCategoryBand? current) =>
        new(LocalModelAdvisorCategoryBandSaveStatus.Conflict, current);
}

public enum LocalModelAdvisorCategoryBandDeleteStatus
{
    Deleted,
    NotFound,
    Conflict,
}

public sealed record LocalModelAdvisorCategoryBandDeleteResult(
    LocalModelAdvisorCategoryBandDeleteStatus Status,
    LocalModelAdvisorCategoryBand? Band)
{
    public bool Succeeded => Status == LocalModelAdvisorCategoryBandDeleteStatus.Deleted;

    public static LocalModelAdvisorCategoryBandDeleteResult Deleted(LocalModelAdvisorCategoryBand deleted) =>
        new(LocalModelAdvisorCategoryBandDeleteStatus.Deleted, deleted);

    public static LocalModelAdvisorCategoryBandDeleteResult NotFound() =>
        new(LocalModelAdvisorCategoryBandDeleteStatus.NotFound, null);

    public static LocalModelAdvisorCategoryBandDeleteResult Conflict(LocalModelAdvisorCategoryBand? current) =>
        new(LocalModelAdvisorCategoryBandDeleteStatus.Conflict, current);
}

public sealed class LocalModelAdvisorCategoryBandSource(
    ILocalModelAdvisorCategoryBandStore? bandStore = null,
    ILocalModelAdvisorDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly ILocalModelAdvisorDiagnostics _diagnostics = diagnostics ?? NullLocalModelAdvisorDiagnostics.Instance;
    private ImmutableDictionary<string, LocalModelAdvisorCategoryBand> _snapshot =
        ImmutableDictionary<string, LocalModelAdvisorCategoryBand>.Empty.WithComparers(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, LocalModelAdvisorCategoryBand> Current => Volatile.Read(ref _snapshot);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (bandStore is null)
        {
            return;
        }

        try
        {
            var bands = await bandStore.ListAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = bands.ToImmutableDictionary(
                band => band.Category,
                band => band,
                StringComparer.Ordinal);
            Interlocked.Exchange(ref _snapshot, snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.RefreshFailed(ex);
        }
    }

    public async Task RunRefreshLoopAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (bandStore is null)
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

    public LocalModelAdvisorValues Resolve(LocalModelAdvisorValues global, string category)
    {
        ArgumentNullException.ThrowIfNull(global);
        if (!LocalModelAdvisorCategoryBandValidator.TryNormalizeCategory(category, out var normalized, out _)
            || !Current.TryGetValue(normalized, out var band))
        {
            return global;
        }

        return global with
        {
            Enabled = band.Enabled ?? global.Enabled,
            InvokeConfidenceMin = band.InvokeConfidenceMin ?? global.InvokeConfidenceMin,
            InvokeConfidenceMax = band.InvokeConfidenceMax ?? global.InvokeConfidenceMax,
            MaxSeverityDelta = band.MaxSeverityDelta ?? global.MaxSeverityDelta,
            MaxConfidenceDelta = band.MaxConfidenceDelta ?? global.MaxConfidenceDelta,
        };
    }
}
