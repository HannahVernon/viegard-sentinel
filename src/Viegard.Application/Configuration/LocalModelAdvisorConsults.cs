using Viegard.Application.Stores;
using Viegard.Domain;

namespace Viegard.Application.Configuration;

public enum AdvisorConsultOutcome
{
    SkippedOutOfBand,
    Escalated,
    NoChange,
    ProviderFailed,
    InvalidOutput,
    DeEscalated,
}

public sealed record AdvisorConsultRecord
{
    public Guid Id { get; init; } = ViegardId.New();

    public required Guid ClassificationId { get; init; }

    public required Guid IncidentId { get; init; }

    public required string Category { get; init; }

    public required AdvisorConsultOutcome Outcome { get; init; }

    public required int BaseSeverity { get; init; }

    public required int FinalSeverity { get; init; }

    public required double BaseConfidence { get; init; }

    public required double FinalConfidence { get; init; }

    public int? LatencyMs { get; init; }

    public string? FailureKind { get; init; }

    public string? ModelId { get; init; }

    public bool ServedFromCache { get; init; }

    public AdvisorEnsembleDetail? EnsembleDetail { get; init; }

    public bool InjectionDetected { get; init; }

    public IReadOnlyList<string> InjectionCategories { get; init; } = [];

    public bool AdvisorSkippedForInjection { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AdvisorEnsembleDetail(string Rule, IReadOnlyList<AdvisorEnsembleModelOutput> Models);

public sealed record AdvisorEnsembleModelOutput(
    string ModelId,
    string Endpoint,
    int? Severity,
    double? Confidence,
    bool Valid);

public sealed record AdvisorOutcomeCount(AdvisorConsultOutcome Outcome, long Count);

public sealed record AdvisorLatencyStats(long Count, int? P50, int? P95);

public interface ILocalModelAdvisorConsultStore
{
    Task AppendAsync(AdvisorConsultRecord record, CancellationToken cancellationToken = default);

    Task<AdvisorConsultRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AdvisorConsultRecord?> GetByClassificationIdAsync(Guid classificationId, CancellationToken cancellationToken = default);

    Task<KeysetPage<AdvisorConsultRecord>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        AdvisorConsultOutcome? outcome,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdvisorOutcomeCount>> GetOutcomeCountsAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    Task<long> GetCacheHitCountAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    Task<long> GetInjectionDetectedCountAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    Task<long> GetSkippedForInjectionCountAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    Task<AdvisorLatencyStats> GetLatencyStatsAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdvisorConsultRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
