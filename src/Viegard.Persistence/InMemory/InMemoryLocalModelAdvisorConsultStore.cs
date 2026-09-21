using Viegard.Application.Configuration;
using Viegard.Application.Stores;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryLocalModelAdvisorConsultStore : ILocalModelAdvisorConsultStore
{
    private readonly Lock _sync = new();
    private readonly List<AdvisorConsultRecord> _records = [];

    public Task AppendAsync(AdvisorConsultRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            _records.Add(Normalize(record));
        }

        return Task.CompletedTask;
    }

    public Task<AdvisorConsultRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(_records.FirstOrDefault(r => r.Id == id));
        }
    }

    public Task<AdvisorConsultRecord?> GetByClassificationIdAsync(Guid classificationId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(_records
                .Where(r => r.ClassificationId == classificationId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefault());
        }
    }

    public Task<KeysetPage<AdvisorConsultRecord>> ListPageAsync(
        Guid? beforeId,
        int pageSize,
        AdvisorConsultOutcome? outcome,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IEnumerable<AdvisorConsultRecord> source = _records;
            if (outcome is not null)
            {
                source = source.Where(r => r.Outcome == outcome.Value);
            }

            return Task.FromResult(InMemoryPaging.Page(source, beforeId, pageSize, r => r.Id));
        }
    }

    public Task<IReadOnlyList<AdvisorOutcomeCount>> GetOutcomeCountsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        lock (_sync)
        {
            IReadOnlyList<AdvisorOutcomeCount> result = _records
                .Where(r => r.CreatedAt >= cutoff)
                .GroupBy(r => r.Outcome)
                .Select(g => new AdvisorOutcomeCount(g.Key, g.LongCount()))
                .OrderBy(g => g.Outcome)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<AdvisorLatencyStats> GetLatencyStatsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        lock (_sync)
        {
            var latencies = _records
                .Where(r => r.CreatedAt >= cutoff && IsSuccessfulCall(r.Outcome) && r.LatencyMs is not null)
                .Select(r => r.LatencyMs!.Value)
                .Order()
                .ToArray();
            return Task.FromResult(ToLatencyStats(latencies));
        }
    }

    public Task<long> GetCacheHitCountAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        lock (_sync)
        {
            return Task.FromResult(_records.LongCount(r => r.CreatedAt >= cutoff && r.ServedFromCache));
        }
    }

    public Task<long> GetInjectionDetectedCountAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        lock (_sync)
        {
            return Task.FromResult(_records.LongCount(r => r.CreatedAt >= cutoff && r.InjectionDetected));
        }
    }

    public Task<long> GetSkippedForInjectionCountAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var cutoff = since.ToUniversalTime();
        lock (_sync)
        {
            return Task.FromResult(_records.LongCount(r => r.CreatedAt >= cutoff && r.AdvisorSkippedForInjection));
        }
    }

    public Task<IReadOnlyList<AdvisorConsultRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_sync)
        {
            IReadOnlyList<AdvisorConsultRecord> result = _records
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var utc = cutoff.ToUniversalTime();
        lock (_sync)
        {
            var removed = _records.RemoveAll(r => r.CreatedAt < utc);
            return Task.FromResult(removed);
        }
    }

    public static AdvisorLatencyStats ToLatencyStats(IReadOnlyList<int> latencies)
    {
        if (latencies.Count == 0)
        {
            return new AdvisorLatencyStats(0, null, null);
        }

        return new AdvisorLatencyStats(latencies.Count, Percentile(latencies, 0.50), Percentile(latencies, 0.95));
    }

    private static int Percentile(IReadOnlyList<int> sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static bool IsSuccessfulCall(AdvisorConsultOutcome outcome) =>
        outcome is AdvisorConsultOutcome.Escalated or AdvisorConsultOutcome.DeEscalated or AdvisorConsultOutcome.NoChange;

    private static AdvisorConsultRecord Normalize(AdvisorConsultRecord record) => record with
    {
        CreatedAt = record.CreatedAt.ToUniversalTime(),
        InjectionCategories = record.InjectionCategories
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
    };
}
