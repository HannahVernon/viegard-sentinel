using Viegard.Application.Coalescing;
using Viegard.Application.Detection;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Correlation;

public sealed class TimeWindowCorrelator(
    IEnumerable<IDetectionRule> detectionRules,
    IIncidentStore incidentStore,
    CorrelationOptions options,
    IncidentCoalescingSettingsSource? coalescingSource = null,
    IncidentCoalescingOptions? coalescingOptions = null) : ICorrelator
{
    private const string EvidenceTruncatedDescription = "Evidence truncated: incident evidence item cap reached.";
    private static readonly IncidentCoalescingValues DisabledCoalescing = new(false, 10, 300);

    private readonly IReadOnlyList<IDetectionRule> _detectionRules = detectionRules.ToList();
    private readonly TimeSpan _windowDuration = options.WindowDuration > TimeSpan.Zero
        ? options.WindowDuration
        : TimeSpan.FromMinutes(10);
    private readonly int _maxEventIds = Math.Max(1, options.MaxEventIdsPerIncident);
    private readonly int _maxEvidenceItems = Math.Max(1, options.MaxEvidenceItemsPerIncident);
    private readonly IncidentCoalescingOptions _coalescingFallback = coalescingOptions ?? new IncidentCoalescingOptions();

    public async ValueTask<IReadOnlyList<Incident>> CorrelateAsync(
        NormalizedEvent normalizedEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);

        var correlationKey = GetCorrelationKey(normalizedEvent);
        if (correlationKey is null)
        {
            return [];
        }

        var evidence = EvaluateRules(normalizedEvent);
        var coalescing = CoalescingValues();

        var existing = coalescing.Enabled
            ? await incidentStore
                .FindCoalescibleByCorrelationKeyAsync(correlationKey, normalizedEvent.OccurredAt, cancellationToken)
                .ConfigureAwait(false)
            : await incidentStore
                .FindOpenByCorrelationKeyAsync(correlationKey, cancellationToken)
                .ConfigureAwait(false);

        if (existing is null)
        {
            if (evidence.Count == 0)
            {
                return [];
            }

            var created = CreateIncident(normalizedEvent, correlationKey, evidence, coalescing);
            await incidentStore.UpsertAsync(created, cancellationToken).ConfigureAwait(false);
            return [created];
        }

        var openEventIds = existing.EventIds ?? [];
        var openEvidence = existing.Evidence ?? [];
        if (openEventIds.Contains(normalizedEvent.Id))
        {
            return [];
        }

        // When coalescing is disabled, absorbability is bounded by the classic
        // sliding window; when enabled, the coalescing find already guarantees
        // the event arrived inside the incident's live coalescing window.
        if (!coalescing.Enabled)
        {
            var withinReach = normalizedEvent.OccurredAt <= existing.WindowEnd.Add(_windowDuration);
            if (!withinReach)
            {
                if (evidence.Count == 0)
                {
                    return [];
                }

                var created = CreateIncident(normalizedEvent, correlationKey, evidence, coalescing);
                await incidentStore.UpsertAsync(created, cancellationToken).ConfigureAwait(false);
                return [created];
            }
        }

        if (evidence.Count == 0 && openEventIds.Count >= _maxEventIds)
        {
            return [];
        }

        var updated = existing with
        {
            WindowStart = Min(existing.WindowStart, normalizedEvent.OccurredAt),
            WindowEnd = Max(existing.WindowEnd, normalizedEvent.OccurredAt),
            EventIds = AppendEventId(openEventIds, normalizedEvent.Id),
            Evidence = AppendEvidence(openEvidence, evidence),
            CoalesceUntil = ExtendCoalesceUntil(existing, normalizedEvent, coalescing),
        };

        await incidentStore.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return [updated];
    }

    private IncidentCoalescingValues CoalescingValues() =>
        coalescingSource is not null
            ? coalescingSource.CurrentValues(_coalescingFallback)
            : DisabledCoalescing;

    private Incident CreateIncident(
        NormalizedEvent normalizedEvent,
        string correlationKey,
        IReadOnlyList<EvidenceItem> evidence,
        IncidentCoalescingValues coalescing) => new()
    {
        Id = ViegardId.New(),
        CorrelationKey = correlationKey,
        WindowStart = normalizedEvent.OccurredAt,
        WindowEnd = normalizedEvent.OccurredAt,
        EventIds = [normalizedEvent.Id],
        Evidence = AppendEvidence([], evidence),
        State = IncidentState.Open,
        CoalesceUntil = coalescing.Enabled
            ? normalizedEvent.OccurredAt + coalescing.SettleWindow
            : null,
    };

    private static DateTimeOffset? ExtendCoalesceUntil(
        Incident existing,
        NormalizedEvent normalizedEvent,
        IncidentCoalescingValues coalescing)
    {
        if (!coalescing.Enabled)
        {
            return existing.CoalesceUntil;
        }

        var candidate = normalizedEvent.OccurredAt + coalescing.SettleWindow;
        var extended = existing.CoalesceUntil is { } current && current > candidate ? current : candidate;
        var windowStart = Min(existing.WindowStart, normalizedEvent.OccurredAt);
        var cap = windowStart + coalescing.MaxCoalesceWindow;
        return extended > cap ? cap : extended;
    }

    private IReadOnlyList<EvidenceItem> EvaluateRules(NormalizedEvent normalizedEvent)
    {
        var evidence = new List<EvidenceItem>();
        foreach (var rule in _detectionRules)
        {
            var ruleEvidence = rule.Evaluate(normalizedEvent) ?? [];
            foreach (var item in ruleEvidence)
            {
                evidence.Add(item.EventId is null ? item with { EventId = normalizedEvent.Id } : item);
            }
        }

        return evidence;
    }

    private IReadOnlyList<Guid> AppendEventId(IReadOnlyList<Guid> existing, Guid eventId)
    {
        if (existing.Contains(eventId) || existing.Count >= _maxEventIds)
        {
            return existing;
        }

        return existing.Concat([eventId]).ToList();
    }

    private IReadOnlyList<EvidenceItem> AppendEvidence(
        IReadOnlyList<EvidenceItem> existing,
        IReadOnlyList<EvidenceItem> incoming)
    {
        if (incoming.Count == 0 || existing.Any(IsTruncationItem))
        {
            return existing;
        }

        var combined = existing.Concat(incoming).ToList();
        if (combined.Count <= _maxEvidenceItems)
        {
            return combined;
        }

        var truncation = new EvidenceItem
        {
            Description = EvidenceTruncatedDescription,
            Score = 0,
        };

        return _maxEvidenceItems == 1
            ? [truncation]
            : combined.Take(_maxEvidenceItems - 1).Concat([truncation]).ToList();
    }

    private static bool IsTruncationItem(EvidenceItem item) =>
        string.Equals(item.Description, EvidenceTruncatedDescription, StringComparison.Ordinal);

    private static string? GetCorrelationKey(NormalizedEvent normalizedEvent) =>
        normalizedEvent.Payload switch
        {
            HttpRequestEvent http => IpKey(PrimaryIp(normalizedEvent) ?? http.RemoteAddress),
            SyslogEvent syslog => IpKey(PrimaryIp(normalizedEvent) ?? syslog.PeerIp),
            MDaemonLogEvent mdaemon => IpKey(PrimaryIp(normalizedEvent) ?? mdaemon.RemoteIp),
            MailMessageEvent mail => MailKey(mail.From.FirstOrDefault()?.Address),
            _ => null,
        };

    private static string? PrimaryIp(NormalizedEvent normalizedEvent) =>
        (normalizedEvent.Entities ?? Array.Empty<EntityRef>()).FirstOrDefault(e =>
            e.Kind == EntityKind.IpAddress && !string.IsNullOrWhiteSpace(e.Value)).Value;

    private static string? IpKey(string? value)
    {
        var cleaned = CleanKeyValue(value);
        return cleaned is null ? null : "ip=" + cleaned;
    }

    private static string? MailKey(string? value)
    {
        var cleaned = CleanKeyValue(value)?.ToLowerInvariant();
        return cleaned is null ? null : "mail-from=" + cleaned;
    }

    private static string? CleanKeyValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) =>
        first >= second ? first : second;

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;
}