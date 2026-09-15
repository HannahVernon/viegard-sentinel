using Microsoft.Extensions.Options;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Classifications;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Classifiers;

/// <summary>
/// Deterministic classifier that converts correlated incident evidence into a
/// schema-valid classification without any inference dependency.
/// </summary>
public sealed class DeterministicIncidentClassifier(
    IIncidentStore incidentStore,
    IOptions<ClassifierOptions> options) : IClassifier
{
    public const string Id = "deterministic-evidence-v1";
    private const int MaxReasons = 10;

    public string ClassifierId => Id;

    public async Task<ClassificationOutcome> ClassifyAsync(
        ClassificationSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (subject.Kind != ClassificationSubjectKind.Incident)
        {
            return ClassificationOutcome.Failure(
                ClassificationFailureKind.Unknown,
                $"Deterministic classifier only supports {ClassificationSubjectKind.Incident} subjects.");
        }

        var incident = await incidentStore.GetAsync(subject.SubjectId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return ClassificationOutcome.Failure(
                ClassificationFailureKind.Unknown,
                $"Incident {subject.SubjectId} was not found.");
        }

        var classifierOptions = options.Value;
        var evidence = incident.Evidence ?? [];
        var totalScore = Math.Max(0.0, evidence.Sum(e => double.IsFinite(e.Score) ? e.Score : 0.0));
        var confidence = Math.Clamp(totalScore / classifierOptions.ScoreForFullConfidence, 0.0, 1.0);
        var severity = (int)Math.Clamp(
            Math.Round(totalScore * classifierOptions.SeverityPerScorePoint, MidpointRounding.AwayFromZero),
            Classification.MinSeverity,
            Classification.MaxSeverity);
        var topEvidence = evidence
            .Where(e => !string.IsNullOrWhiteSpace(e.Description))
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.Description, StringComparer.Ordinal)
            .ToList();

        var classification = new Classification
        {
            Id = ViegardId.New(),
            SubjectKind = ClassificationSubjectKind.Incident,
            SubjectId = incident.Id,
            ClassifierId = ClassifierId,
            Model = null,
            Category = DeriveCategory(topEvidence),
            Confidence = confidence,
            Severity = severity,
            Reasons = topEvidence
                .Select(e => e.Description)
                .Take(MaxReasons)
                .ToList(),
            RecommendedAction = RecommendAction(incident, totalScore, classifierOptions),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        return ClassificationOutcome.Success(classification);
    }

    private static string DeriveCategory(IReadOnlyList<EvidenceItem> topEvidence)
    {
        foreach (var item in topEvidence)
        {
            var description = item.Description;
            if (Contains(description, "Blocking IP")
                || Contains(description, "blocked the IP")
                || Contains(description, "auth"))
            {
                return "credential-attack";
            }

            if (Contains(description, "traversal"))
            {
                return "path-traversal";
            }

            if (Contains(description, "scanner") || Contains(description, "user-agent"))
            {
                return "vulnerability-scanner";
            }

            if (Contains(description, "SQL"))
            {
                return "sql-injection";
            }

            if (Contains(description, "command"))
            {
                return "command-injection";
            }

            if (Contains(description, "sensitive path") || Contains(description, "probe"))
            {
                return "reconnaissance";
            }
        }

        return "suspicious-activity";
    }

    private static string RecommendAction(Incident incident, double totalScore, ClassifierOptions options) =>
        incident.CorrelationKey.StartsWith("ip=", StringComparison.OrdinalIgnoreCase)
            && totalScore >= options.BlockRecommendationScore
            ? "block-source-ip"
            : "flag-for-review";

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);
}