using Viegard.Domain.Configuration;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Detection;

internal static class DetectionText
{
    public static string Limit(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safeMax = Math.Clamp(maxChars, 1, 65_536);
        return value.Length <= safeMax ? value : value[..safeMax];
    }

    public static string DecodeRepeated(string value, int passes = 2)
    {
        var current = value;
        for (var i = 0; i < passes; i++)
        {
            try
            {
                var decoded = Uri.UnescapeDataString(current);
                if (string.Equals(decoded, current, StringComparison.Ordinal))
                {
                    return current;
                }

                current = decoded;
            }
            catch (UriFormatException)
            {
                return current;
            }
        }

        return current;
    }

    public static string Display(string value, int maxChars = 80)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ');
        return cleaned.Length <= maxChars ? cleaned : cleaned[..maxChars] + "...";
    }

    public static EvidenceItem Evidence(string ruleId, string description, double score, Guid eventId) => new()
    {
        Description = $"Rule {ruleId}: {description}",
        // The ceiling matches the maximum signature evidence weight.  It was
        // 1.0 until the D-0029 weight cap rose to 5.0; the old ceiling here
        // silently flattened every high-weight signature back to 1.0, which
        // kept single-event matches out of the review and action bands
        // (found live: weight-4 and weight-5 signatures both scored 1.0).
        Score = Math.Clamp(score, 0.0, CustomSignature.MaxEvidenceWeight),
        EventId = eventId,
    };

    public static IReadOnlyList<EvidenceItem> Cap(IReadOnlyList<EvidenceItem> evidence, DetectionOptions options)
    {
        var max = Math.Max(1, options.MaxEvidencePerRule);
        return evidence.Count <= max ? evidence : evidence.Take(max).ToList();
    }

    public static IEnumerable<ScoredDetectionTerm> ValidTerms(IEnumerable<ScoredDetectionTerm> terms) =>
        terms.Where(t => !string.IsNullOrWhiteSpace(t.Value));
}
