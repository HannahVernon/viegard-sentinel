using Viegard.Application.Detection;
using Viegard.Application.Stores;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Correlation;

public sealed class TimeWindowCorrelator(
    IEnumerable<IDetectionRule> detectionRules,
    IIncidentStore incidentStore,
    CorrelationOptions options) : ICorrelator
{
    private const string EvidenceTruncatedDescription = "Evidence truncated: incident evidence item cap reached.";

    private readonly IReadOnlyList<IDetectionRule> _detectionRules = detectionRules.ToList();
    private readonly TimeSpan _windowDuration = options.WindowDuration > TimeSpan.Zero
        ? options.WindowDuration
        : TimeSpan.FromMinutes(10);
    private readonly int _maxEventIds = Math.Max(1, options.MaxEventIdsPerIncident);
    private readonly int _maxEvidenceItems = Math.Max(1, options.MaxEvidenceItemsPerIncident);

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
        var open = await incidentStore.FindOpenByCorrelationKeyAsync(correlationKey, cancellationToken)
            .ConfigureAwait(false);

        if (open is null)
        {
            if (evidence.Count == 0)
            {
                return [];
            }

            var created = CreateIncident(normalizedEvent, correlationKey, evidence);
            await incidentStore.UpsertAsync(created, cancellationToken).ConfigureAwait(false);
            return [created];
        }

        var openEventIds = open.EventIds ?? [];
        var openEvidence = open.Evidence ?? [];
        if (openEventIds.Contains(normalizedEvent.Id))
        {
            return [];
        }

        var withinReach = normalizedEvent.OccurredAt <= open.WindowEnd.Add(_windowDuration);
        if (!withinReach)
        {
            if (evidence.Count == 0)
            {
                return [];
            }

            var created = CreateIncident(normalizedEvent, correlationKey, evidence);
            await incidentStore.UpsertAsync(created, cancellationToken).ConfigureAwait(false);
            return [created];
        }

        if (evidence.Count == 0 && openEventIds.Count >= _maxEventIds)
        {
            return [];
        }

        var updated = open with
        {
            WindowStart = Min(open.WindowStart, normalizedEvent.OccurredAt),
            WindowEnd = Max(open.WindowEnd, normalizedEvent.OccurredAt),
            EventIds = AppendEventId(openEventIds, normalizedEvent.Id),
            Evidence = AppendEvidence(openEvidence, evidence),
        };

        await incidentStore.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return [updated];
    }

    private Incident CreateIncident(
        NormalizedEvent normalizedEvent,
        string correlationKey,
        IReadOnlyList<EvidenceItem> evidence) => new()
    {
        Id = Guid.NewGuid(),
        CorrelationKey = correlationKey,
        WindowStart = normalizedEvent.OccurredAt,
        WindowEnd = normalizedEvent.OccurredAt,
        EventIds = [normalizedEvent.Id],
        Evidence = AppendEvidence([], evidence),
        State = IncidentState.Open,
    };

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
