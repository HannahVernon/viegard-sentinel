using Microsoft.Extensions.Options;

namespace Viegard.Application.Configuration;

/// <summary>
/// Database-owned local-model advisor settings.  Seeded once from deployment
/// configuration, then edited on /configuration.
/// </summary>
public sealed record LocalModelAdvisorSettings
{
    public const int FixedId = 1;
    public const int MaxEndpointLength = 2048;
    public const int MaxModelLength = 128;
    public const int MaxKeepAliveLength = 64;
    public const int MaxUpdatedByLength = 128;
    public const string DefaultEndpoint = "http://127.0.0.1:11434";
    public const string DefaultModel = "qwen2.5:7b-instruct";
    public const string DefaultSecondModelEndpoint = DefaultEndpoint;
    public const string DefaultKeepAlive = "5m";
    public const string SystemSeedActor = "system:local-model-advisor-seed";

    public int Id { get; init; } = FixedId;

    public bool Enabled { get; init; }

    public string Endpoint { get; init; } = DefaultEndpoint;

    public string Model { get; init; } = DefaultModel;

    public double Temperature { get; init; }

    public int TimeoutMs { get; init; } = 8000;

    public string KeepAlive { get; init; } = DefaultKeepAlive;

    public double InvokeConfidenceMin { get; init; } = 0.50;

    public double InvokeConfidenceMax { get; init; } = 0.85;

    public int MaxSeverityDelta { get; init; } = 3;

    public double MaxConfidenceDelta { get; init; } = 0.20;

    public bool ResponseCacheEnabled { get; init; }

    public int ResponseCacheTtlHours { get; init; } = 72;

    public bool EnsembleEnabled { get; init; }

    public string SecondModelEndpoint { get; init; } = DefaultSecondModelEndpoint;

    public string SecondModel { get; init; } = string.Empty;

    public AdvisorInjectionAction InjectionAction { get; init; } = AdvisorInjectionAction.SkipAdvisor;

    public bool DeEscalationEnabled { get; init; }

    public int MaxDownwardSeverityDelta { get; init; } = 1;

    public double MaxDownwardConfidenceDelta { get; init; } = 0.10;

    public double DeEscalationMinModelConfidence { get; init; } = 0.70;

    public int DeEscalationProtectedSeverity { get; init; } = 7;

    public int Version { get; init; }

    public DateTimeOffset? SeededAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public static LocalModelAdvisorSettings FromOptions(LocalModelAdvisorOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new LocalModelAdvisorSettings
        {
            Id = FixedId,
            Enabled = options.Enabled,
            Endpoint = options.Endpoint,
            Model = options.Model,
            Temperature = options.Temperature,
            TimeoutMs = options.TimeoutMs,
            KeepAlive = options.KeepAlive,
            InvokeConfidenceMin = options.InvokeConfidenceMin,
            InvokeConfidenceMax = options.InvokeConfidenceMax,
            MaxSeverityDelta = options.MaxSeverityDelta,
            MaxConfidenceDelta = options.MaxConfidenceDelta,
            ResponseCacheEnabled = options.ResponseCacheEnabled,
            ResponseCacheTtlHours = options.ResponseCacheTtlHours,
            EnsembleEnabled = options.EnsembleEnabled,
            SecondModelEndpoint = options.SecondModelEndpoint,
            SecondModel = options.SecondModel,
            InjectionAction = options.InjectionAction,
            DeEscalationEnabled = options.DeEscalationEnabled,
            MaxDownwardSeverityDelta = options.MaxDownwardSeverityDelta,
            MaxDownwardConfidenceDelta = options.MaxDownwardConfidenceDelta,
            DeEscalationMinModelConfidence = options.DeEscalationMinModelConfidence,
            DeEscalationProtectedSeverity = options.DeEscalationProtectedSeverity,
            Version = 1,
            SeededAt = utc,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public static class LocalModelAdvisorSettingsValidator
{
    public const string EndpointError = "Local-model advisor endpoint must be an absolute http or https URL.";
    public const string ModelError = "Local-model advisor model is required, must not exceed 128 characters, and must not contain control characters.";
    public const string TemperatureError = "Local-model advisor temperature must be between 0 and 2.";
    public const string TimeoutError = "Local-model advisor timeout must be between 250 and 120000 milliseconds.";
    public const string KeepAliveError = "Local-model advisor keep-alive must not exceed 64 characters and must not contain control characters.";
    public const string ConfidenceBandError = "Local-model advisor confidence band must satisfy 0 <= min <= max <= 1.";
    public const string MaxSeverityDeltaError = "Local-model advisor max severity delta must be between 0 and 10.";
    public const string MaxConfidenceDeltaError = "Local-model advisor max confidence delta must be between 0 and 1.";
    public const string ResponseCacheTtlError = "Local-model advisor response cache TTL must be between 1 and 2160 hours.";
    public const string SecondModelEndpointError = "Local-model advisor second model endpoint must be an absolute http or https URL.";
    public const string SecondModelError = "Local-model advisor second model is required when ensemble is enabled, must not exceed 128 characters, and must not contain control characters.";
    public const string InjectionActionError = "Local-model advisor injection action must be RecordOnly or SkipAdvisor.";
    public const string MaxDownwardSeverityDeltaError = "Local-model advisor max downward severity delta must be between 0 and 10.";
    public const string MaxDownwardConfidenceDeltaError = "Local-model advisor max downward confidence delta must be between 0 and 1.";
    public const string DeEscalationMinModelConfidenceError = "Local-model advisor de-escalation minimum model confidence must be between 0 and 1.";
    public const string DeEscalationProtectedSeverityError = "Local-model advisor de-escalation protected severity must be between 0 and 10.";

    public static bool TryValidate(LocalModelAdvisorSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryNormalizeEndpoint(settings.Endpoint, out _, out error)
            || !TryNormalizeModel(settings.Model, out _, out error)
            || !TryNormalizeKeepAlive(settings.KeepAlive, out _, out error))
        {
            return false;
        }

        if (!double.IsFinite(settings.Temperature) || settings.Temperature is < 0.0 or > 2.0)
        {
            error = TemperatureError;
            return false;
        }

        if (settings.TimeoutMs is < 250 or > 120_000)
        {
            error = TimeoutError;
            return false;
        }

        if (!double.IsFinite(settings.InvokeConfidenceMin)
            || !double.IsFinite(settings.InvokeConfidenceMax)
            || settings.InvokeConfidenceMin < 0.0
            || settings.InvokeConfidenceMax > 1.0
            || settings.InvokeConfidenceMin > settings.InvokeConfidenceMax)
        {
            error = ConfidenceBandError;
            return false;
        }

        if (settings.MaxSeverityDelta is < 0 or > 10)
        {
            error = MaxSeverityDeltaError;
            return false;
        }

        if (!double.IsFinite(settings.MaxConfidenceDelta) || settings.MaxConfidenceDelta is < 0.0 or > 1.0)
        {
            error = MaxConfidenceDeltaError;
            return false;
        }

        if (settings.ResponseCacheTtlHours is < 1 or > 2160)
        {
            error = ResponseCacheTtlError;
            return false;
        }

        if (!TryNormalizeOptionalEndpoint(
                settings.SecondModelEndpoint,
                required: settings.EnsembleEnabled,
                out _,
                out error))
        {
            return false;
        }

        if (!TryNormalizeOptionalModel(
                settings.SecondModel,
                required: settings.EnsembleEnabled,
                out _,
                out error))
        {
            return false;
        }

        if (!Enum.IsDefined(settings.InjectionAction))
        {
            error = InjectionActionError;
            return false;
        }

        if (settings.MaxDownwardSeverityDelta is < 0 or > 10)
        {
            error = MaxDownwardSeverityDeltaError;
            return false;
        }

        if (!double.IsFinite(settings.MaxDownwardConfidenceDelta) || settings.MaxDownwardConfidenceDelta is < 0.0 or > 1.0)
        {
            error = MaxDownwardConfidenceDeltaError;
            return false;
        }

        if (!double.IsFinite(settings.DeEscalationMinModelConfidence) || settings.DeEscalationMinModelConfidence is < 0.0 or > 1.0)
        {
            error = DeEscalationMinModelConfidenceError;
            return false;
        }

        if (settings.DeEscalationProtectedSeverity is < 0 or > 10)
        {
            error = DeEscalationProtectedSeverityError;
            return false;
        }

        if (settings.Id != LocalModelAdvisorSettings.FixedId)
        {
            error = "Local-model advisor settings row has an invalid id.";
            return false;
        }

        if (settings.Version < 0)
        {
            error = "Local-model advisor settings version must not be negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeSecondModelEndpoint(string? value, bool required, out string normalized, out string error) =>
        TryNormalizeOptionalEndpoint(value, required, out normalized, out error);

    public static bool TryNormalizeSecondModel(string? value, bool required, out string normalized, out string error) =>
        TryNormalizeOptionalModel(value, required, out normalized, out error);

    public static bool TryNormalizeInjectionAction(string? value, out AdvisorInjectionAction normalized, out string error)
    {
        if (Enum.TryParse<AdvisorInjectionAction>(value?.Trim(), ignoreCase: false, out normalized)
            && Enum.IsDefined(normalized))
        {
            error = string.Empty;
            return true;
        }

        normalized = AdvisorInjectionAction.SkipAdvisor;
        error = InjectionActionError;
        return false;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= LocalModelAdvisorSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..LocalModelAdvisorSettings.MaxUpdatedByLength];
    }

    public static bool TryNormalizeEndpoint(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = EndpointError;
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > LocalModelAdvisorSettings.MaxEndpointLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = EndpointError;
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeModel(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = ModelError;
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length == 0
            || candidate.Length > LocalModelAdvisorSettings.MaxModelLength
            || candidate.Any(char.IsControl))
        {
            error = ModelError;
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeKeepAlive(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length > LocalModelAdvisorSettings.MaxKeepAliveLength || candidate.Any(char.IsControl))
        {
            error = KeepAliveError;
            return false;
        }

        normalized = candidate.Length == 0 ? LocalModelAdvisorSettings.DefaultKeepAlive : candidate;
        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeOptionalEndpoint(string? value, bool required, out string normalized, out string error)
    {
        if (!required && string.IsNullOrWhiteSpace(value))
        {
            normalized = LocalModelAdvisorSettings.DefaultSecondModelEndpoint;
            error = string.Empty;
            return true;
        }

        if (TryNormalizeEndpoint(value, out normalized, out _))
        {
            error = string.Empty;
            return true;
        }

        error = SecondModelEndpointError;
        return false;
    }

    private static bool TryNormalizeOptionalModel(string? value, bool required, out string normalized, out string error)
    {
        if (!required && string.IsNullOrWhiteSpace(value))
        {
            normalized = string.Empty;
            error = string.Empty;
            return true;
        }

        if (TryNormalizeModel(value, out normalized, out _))
        {
            error = string.Empty;
            return true;
        }

        error = SecondModelError;
        return false;
    }
}

public interface ILocalModelAdvisorSettingsStore
{
    long CurrentChangeVersion { get; }

    ValueTask<LocalModelAdvisorSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorSettingsCreateResult> TryCreateAsync(
        LocalModelAdvisorSettings settings,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorSettingsSaveResult> UpsertAsync(
        LocalModelAdvisorSettings settings,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<LocalModelAdvisorSettings?> SeedIfMissingAsync(
        LocalModelAdvisorOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default);

    ValueTask<long> WaitForChangeAsync(
        long lastSeenVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record LocalModelAdvisorSettingsCreateResult(bool Created, LocalModelAdvisorSettings Settings);

public enum LocalModelAdvisorSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record LocalModelAdvisorSettingsSaveResult(
    LocalModelAdvisorSettingsSaveStatus Status,
    LocalModelAdvisorSettings? Settings)
{
    public bool Succeeded => Status == LocalModelAdvisorSettingsSaveStatus.Saved;

    public static LocalModelAdvisorSettingsSaveResult Saved(LocalModelAdvisorSettings settings) =>
        new(LocalModelAdvisorSettingsSaveStatus.Saved, settings);

    public static LocalModelAdvisorSettingsSaveResult Conflict(LocalModelAdvisorSettings? current) =>
        new(LocalModelAdvisorSettingsSaveStatus.Conflict, current);
}
