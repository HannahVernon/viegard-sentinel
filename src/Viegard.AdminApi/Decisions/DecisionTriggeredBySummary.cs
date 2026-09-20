using Viegard.Domain.Classifications;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.AdminApi.Decisions;

public static class DecisionTriggeredBySummary
{
    private const string NoSummary = "-";
    private const string EvidenceTruncatedDescription = "Evidence truncated: incident evidence item cap reached.";

    public static string Summarize(
        Classification? classification,
        Incident? incident,
        IReadOnlyDictionary<Guid, NormalizedEvent> eventsById)
    {
        ArgumentNullException.ThrowIfNull(eventsById);

        if (classification is null)
        {
            return NoSummary;
        }

        if (classification.SubjectKind == ClassificationSubjectKind.MailMessage)
        {
            return "mail/message";
        }

        if (incident is null)
        {
            return NoSummary;
        }

        var eventKinds = SummarizeEventKinds(incident, eventsById);
        return string.IsNullOrWhiteSpace(eventKinds)
            ? SummarizeEvidence(incident.Evidence)
            : eventKinds;
    }

    private static string SummarizeEventKinds(
        Incident incident,
        IReadOnlyDictionary<Guid, NormalizedEvent> eventsById)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var eventId in EventIds(incident))
        {
            if (!eventsById.TryGetValue(eventId, out var normalizedEvent))
            {
                continue;
            }

            var kind = EventKindName.Of(normalizedEvent.Payload);
            if (string.IsNullOrWhiteSpace(kind))
            {
                continue;
            }

            counts[kind] = counts.TryGetValue(kind, out var count) ? count + 1 : 1;
        }

        if (counts.Count == 0)
        {
            return string.Empty;
        }

        var items = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        var first = FormatKind(items[0].Key, items[0].Value);
        if (items.Length == 1)
        {
            return first;
        }

        var second = FormatKind(items[1].Key, items[1].Value);
        return items.Length == 2
            ? $"{first}, {second}"
            : $"{first}, {second} +{items.Length - 2} more";
    }

    private static IEnumerable<Guid> EventIds(Incident incident)
    {
        var seen = new HashSet<Guid>();
        foreach (var eventId in incident.EventIds)
        {
            if (seen.Add(eventId))
            {
                yield return eventId;
            }
        }

        foreach (var eventId in incident.Evidence
            .Where(item => item.EventId is not null)
            .Select(item => item.EventId!.Value))
        {
            if (seen.Add(eventId))
            {
                yield return eventId;
            }
        }
    }

    private static string SummarizeEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        var items = evidence
            .Select(item => new
            {
                Description = NormalizeEvidence(item.Description),
                item.Score,
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Description))
            .GroupBy(item => item.Description!, StringComparer.Ordinal)
            .Select(group => new
            {
                Description = group.Key,
                Count = group.Count(),
                MaxScore = group.Max(item => item.Score),
            })
            .OrderByDescending(item => item.MaxScore)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Description, StringComparer.Ordinal)
            .ToArray();

        if (items.Length == 0)
        {
            return NoSummary;
        }

        var first = Truncate(items[0].Description, 72);
        return items[0].Count > 1
            ? $"{first} x{items[0].Count}"
            : first;
    }

    private static string NormalizeEvidence(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var normalized = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.Equals(normalized, EvidenceTruncatedDescription, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("Rule ", StringComparison.Ordinal))
        {
            var separator = normalized.IndexOf(": ", StringComparison.Ordinal);
            if (separator > 5)
            {
                normalized = normalized[(separator + 2)..];
            }
        }

        return normalized;
    }

    private static string FormatKind(string kind, int count) =>
        count > 1 ? $"{kind} x{count}" : kind;

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "...";
}
