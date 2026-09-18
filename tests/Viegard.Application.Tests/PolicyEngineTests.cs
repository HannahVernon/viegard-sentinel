using Microsoft.Extensions.Options;
using Viegard.Application.Policy;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class PolicyEngineTests
{
    private static readonly PolicyContext ActiveContext = new()
    {
        DryRun = false,
        ManualApprovalMode = false,
        EmergencyStop = false,
    };

    [Fact]
    public async Task Emergency_stop_denies_everything()
    {
        var fixture = await CreateFixtureAsync();
        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 1.0, severity: 10),
            ActiveContext with { EmergencyStop = true });

        Assert.Equal(DecisionOutcome.RecordOnly, decision.Outcome);
        Assert.Contains("Emergency stop", decision.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dry_run_converts_permit_to_dryrun_but_never_converts_deny()
    {
        var fixture = await CreateFixtureAsync();

        var wouldAct = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext with { DryRun = true });
        var recordOnly = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.5, severity: 8),
            ActiveContext with { DryRun = true });

        Assert.Equal(DecisionOutcome.DryRun, wouldAct.Outcome);
        Assert.Equal(DecisionOutcome.RecordOnly, recordOnly.Outcome);
    }

    [Fact]
    public async Task Manual_approval_mode_converts_permit_to_requireapproval()
    {
        var fixture = await CreateFixtureAsync();

        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext with { ManualApprovalMode = true });

        Assert.Equal(DecisionOutcome.RequireApproval, decision.Outcome);
    }

    [Fact]
    public async Task Manual_approval_mode_wins_over_dry_run_so_reviews_exist_under_dry_run_posture()
    {
        var fixture = await CreateFixtureAsync();

        var wouldAct = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext with { DryRun = true, ManualApprovalMode = true });
        var recordOnly = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.5, severity: 8),
            ActiveContext with { DryRun = true, ManualApprovalMode = true });

        Assert.Equal(DecisionOutcome.RequireApproval, wouldAct.Outcome);
        Assert.Equal(DecisionOutcome.RecordOnly, recordOnly.Outcome);
    }

    [Fact]
    public async Task Ai_thresholds_distinguish_action_review_and_record_only_bands()
    {
        var fixture = await CreateFixtureAsync();

        var action = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext);
        var review = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.8, severity: 8),
            ActiveContext);
        var recordOnly = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.5, severity: 8),
            ActiveContext);

        Assert.Equal(DecisionOutcome.ActionAuthorized, action.Outcome);
        Assert.Equal(DecisionOutcome.RequireApproval, review.Outcome);
        Assert.Equal(DecisionOutcome.RecordOnly, recordOnly.Outcome);
    }

    [Fact]
    public async Task Policy_engine_reads_current_policy_threshold_snapshot()
    {
        var thresholdStore = new InMemoryPolicyThresholdSettingsStore();
        var thresholdSource = new PolicyThresholdSource(thresholdStore);
        var now = new DateTimeOffset(2026, 9, 16, 14, 30, 0, TimeSpan.Zero);
        var created = await thresholdStore.UpdateAsync(
            new PolicyThresholdSettings
            {
                ReviewConfidence = 0.6,
                ActionConfidence = 0.8,
                ActionMinSeverity = 7,
                UpdatedAt = now,
                UpdatedBy = "test",
            },
            expectedRowVersion: 0,
            updatedBy: "hannah",
            updatedAt: now);
        Assert.True(created.Succeeded);
        var createdSettings = created.Settings!;
        await thresholdSource.RefreshAsync();
        var fixture = await CreateFixtureAsync(thresholdSource: thresholdSource);

        var first = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.75, severity: 7),
            ActiveContext);
        Assert.Equal(DecisionOutcome.RequireApproval, first.Outcome);

        var updated = await thresholdStore.UpdateAsync(
            createdSettings with
            {
                ReviewConfidence = 0.8,
                ActionConfidence = 0.95,
                ActionMinSeverity = 7,
            },
            expectedRowVersion: createdSettings.RowVersion,
            updatedBy: "hannah",
            updatedAt: now.AddMinutes(1));
        Assert.True(updated.Succeeded);
        await thresholdSource.RefreshAsync();

        var afterRefresh = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.75, severity: 7),
            ActiveContext);
        Assert.Equal(DecisionOutcome.RecordOnly, afterRefresh.Outcome);
        var thresholds = Assert.Single(afterRefresh.Guardrails, guardrail => guardrail.GuardrailName == PolicyGuardrailNames.Thresholds);
        Assert.Contains("review requires confidence >= 0.8", thresholds.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deterministic_classification_uses_normalized_evidence_confidence_bands()
    {
        var fixture = await CreateFixtureAsync();

        var action = await fixture.Engine.EvaluateAsync(
            DeterministicClassification(fixture.IncidentId, confidence: 0.95, severity: 2),
            ActiveContext);
        var review = await fixture.Engine.EvaluateAsync(
            DeterministicClassification(fixture.IncidentId, confidence: 0.8, severity: 2),
            ActiveContext);

        Assert.Equal(DecisionOutcome.ActionAuthorized, action.Outcome);
        Assert.Equal(DecisionOutcome.RequireApproval, review.Outcome);
        Assert.Contains("Raw evidence threshold", action.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Protected_ip_incident_denies_even_at_max_confidence()
    {
        var fixture = await CreateFixtureAsync("10.0.0.5");

        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 1.0, severity: 10),
            ActiveContext);

        Assert.Equal(DecisionOutcome.RecordOnly, decision.Outcome);
        var protectedGuardrail = Assert.Single(
            decision.Guardrails,
            g => g.GuardrailName == PolicyGuardrailNames.ProtectedAddress);
        Assert.False(protectedGuardrail.Passed);
    }

    [Fact]
    public async Task Policy_engine_extracts_target_ip_from_prefixed_correlation_key()
    {
        var fixture = await CreateFixtureAsync("198.51.100.10|window=60s");

        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext);

        Assert.Equal(DecisionOutcome.ActionAuthorized, decision.Outcome);
        var protectedGuardrail = Assert.Single(
            decision.Guardrails,
            g => g.GuardrailName == PolicyGuardrailNames.ProtectedAddress);
        Assert.True(protectedGuardrail.Passed);
        Assert.Contains("198.51.100.10", protectedGuardrail.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorized_decisions_carry_target_ip_and_duration_for_the_dispatcher()
    {
        // The unattended dispatcher acts on these structured fields; the
        // first live ActionAuthorized decision carried neither and nothing
        // happened, so this pins the contract.
        var fixture = await CreateFixtureAsync("198.51.100.44");

        var authorized = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext);
        var dryRun = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext with { DryRun = true });
        var review = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext with { ManualApprovalMode = true });
        var recordOnly = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.5, severity: 8),
            ActiveContext);

        Assert.Equal(DecisionOutcome.ActionAuthorized, authorized.Outcome);
        Assert.Equal("198.51.100.44", authorized.AuthorizedTargetIp);
        Assert.NotNull(authorized.RecommendedActionDuration);
        Assert.True(authorized.RecommendedActionDuration > TimeSpan.Zero);

        Assert.Equal(DecisionOutcome.DryRun, dryRun.Outcome);
        Assert.Equal("198.51.100.44", dryRun.AuthorizedTargetIp);
        Assert.NotNull(dryRun.RecommendedActionDuration);

        Assert.Equal(DecisionOutcome.RequireApproval, review.Outcome);
        Assert.Null(review.AuthorizedTargetIp);
        Assert.Null(review.RecommendedActionDuration);

        Assert.Equal(DecisionOutcome.RecordOnly, recordOnly.Outcome);
        Assert.Null(recordOnly.AuthorizedTargetIp);
        Assert.Null(recordOnly.RecommendedActionDuration);
    }

    [Fact]
    public async Task Rate_cap_exceeded_requires_approval()
    {
        var fixture = await CreateFixtureAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            await fixture.GuardrailState.RecordAutoActionAsync(now.AddMinutes(-i));
        }

        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext);

        Assert.Equal(DecisionOutcome.RequireApproval, decision.Outcome);
        Assert.Contains(decision.Guardrails, g => g.GuardrailName == PolicyGuardrailNames.RateCaps && !g.Passed);
    }

    [Fact]
    public async Task Circuit_breaker_open_requires_approval()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.GuardrailState.SetCircuitBreakerOpenAsync(true);

        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext);

        Assert.Equal(DecisionOutcome.RequireApproval, decision.Outcome);
        Assert.Contains(decision.Guardrails, g => g.GuardrailName == PolicyGuardrailNames.CircuitBreaker && !g.Passed);
    }

    [Fact]
    public async Task Decisions_include_full_guardrail_chain()
    {
        var fixture = await CreateFixtureAsync();

        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext);

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

    [Fact]
    public async Task Repeat_offender_lookback_changes_recommended_duration_detail()
    {
        var fixture = await CreateFixtureAsync("198.51.100.88");
        var now = DateTimeOffset.UtcNow;
        await fixture.GuardrailState.RecordIncidentAsync("198.51.100.88", Guid.NewGuid(), now.AddDays(-1));
        await fixture.GuardrailState.RecordIncidentAsync("198.51.100.88", Guid.NewGuid(), now.AddDays(-2));

        var decision = await fixture.Engine.EvaluateAsync(
            AiClassification(fixture.IncidentId, confidence: 0.95, severity: 8),
            ActiveContext);

        Assert.Contains("Repeat-offender", decision.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7.00:00:00", decision.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowlisted_structured_sender_denies_automated_action()
    {
        var options = new PolicyOptions();
        options.AllowedSenders.Add("trusted@example.com");
        var fixture = await CreateFixtureAsync(options: options);
        var mailEventId = await AddMailEventAsync(fixture.EventStore, "trusted@example.com");

        var decision = await fixture.Engine.EvaluateAsync(
            MailClassification(mailEventId, confidence: 0.95, severity: 8),
            ActiveContext);

        Assert.Equal(DecisionOutcome.RecordOnly, decision.Outcome);
        var allowlist = Assert.Single(decision.Guardrails, g => g.GuardrailName == PolicyGuardrailNames.Allowlist);
        Assert.False(allowlist.Passed);
    }

    [Fact]
    public async Task Sender_markers_in_classification_text_cannot_trigger_the_allowlist()
    {
        // Regression for the 2026-08-25 security-audit finding: the sender
        // must come from the structured mail event, never from free-form
        // classification text that derives from attacker-controlled mail.
        var options = new PolicyOptions();
        options.AllowedSenders.Add("trusted@example.com");
        var fixture = await CreateFixtureAsync(options: options);
        var mailEventId = await AddMailEventAsync(fixture.EventStore, "attacker@evil.example");

        var classification = MailClassification(mailEventId, confidence: 0.95, severity: 8) with
        {
            Reasons = ["message body contained sender=trusted@example.com from=trusted@example.com"],
        };

        var decision = await fixture.Engine.EvaluateAsync(classification, ActiveContext);

        var allowlist = Assert.Single(decision.Guardrails, g => g.GuardrailName == PolicyGuardrailNames.Allowlist);
        Assert.True(allowlist.Passed);
        Assert.NotEqual(DecisionOutcome.RecordOnly, decision.Outcome);
    }

    [Fact]
    public async Task Missing_mail_event_means_allowlist_cannot_match()
    {
        var options = new PolicyOptions();
        options.AllowedSenders.Add("trusted@example.com");
        var fixture = await CreateFixtureAsync(options: options);

        var decision = await fixture.Engine.EvaluateAsync(
            MailClassification(Guid.NewGuid(), confidence: 0.95, severity: 8),
            ActiveContext);

        var allowlist = Assert.Single(decision.Guardrails, g => g.GuardrailName == PolicyGuardrailNames.Allowlist);
        Assert.True(allowlist.Passed);
        Assert.Contains("cannot be verified", allowlist.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allowlisted_sender_domain_denies_automated_action()
    {
        var options = new PolicyOptions();
        options.AllowedDomains.Add("example.com");
        var fixture = await CreateFixtureAsync(options: options);
        var mailEventId = await AddMailEventAsync(fixture.EventStore, "someone@mail.example.com");

        var decision = await fixture.Engine.EvaluateAsync(
            MailClassification(mailEventId, confidence: 0.95, severity: 8),
            ActiveContext);

        Assert.Equal(DecisionOutcome.RecordOnly, decision.Outcome);
    }

    private static async Task<Guid> AddMailEventAsync(InMemoryEventStore eventStore, string fromAddress)
    {
        var mailEvent = new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = "imap:test-account",
            SourceType = "imap",
            OccurredAt = DateTimeOffset.UtcNow,
            Entities = [],
            Payload = new MailMessageEvent
            {
                AccountId = "test-account",
                Folder = "INBOX",
                Uid = 1,
                From = [new MailAddressInfo { Address = fromAddress }],
                Subject = "test",
            },
            RawObservationId = Guid.NewGuid(),
        };
        await eventStore.AddAsync(mailEvent);
        return mailEvent.Id;
    }

    private static Classification MailClassification(Guid subjectId, double confidence, int severity) =>
        AiClassification(subjectId, confidence, severity) with
        {
            SubjectKind = ClassificationSubjectKind.MailMessage,
            Category = "spam",
            RecommendedAction = "move-to-spam",
        };

    private static async Task<PolicyFixture> CreateFixtureAsync(
        string ip = "198.51.100.10",
        PolicyOptions? options = null,
        PolicyThresholdSource? thresholdSource = null)
    {
        options ??= new PolicyOptions();
        var incidentStore = new InMemoryIncidentStore();
        var eventStore = new InMemoryEventStore();
        var guardrailState = new InMemoryGuardrailStateStore();
        var incident = Incident(ip);
        await incidentStore.UpsertAsync(incident);

        return new PolicyFixture(
            new DefaultPolicyEngine(
                Options.Create(options),
                thresholdSource ?? new PolicyThresholdSource(),
                new ProtectedAddressList(options.ProtectedCidrs),
                incidentStore,
                eventStore,
                guardrailState),
            guardrailState,
            eventStore,
            incident.Id);
    }

    private static Classification AiClassification(Guid subjectId, double confidence, int severity) =>
        Classification(subjectId, confidence, severity) with
        {
            Model = new ModelInfo
            {
                ModelId = "test-model",
                ModelVersion = "0.1",
                PromptTemplateVersion = "test",
            },
        };

    private static Classification DeterministicClassification(Guid subjectId, double confidence, int severity) =>
        Classification(subjectId, confidence, severity);

    private static Classification Classification(Guid subjectId, double confidence, int severity) =>
        new()
        {
            Id = Guid.NewGuid(),
            SubjectKind = ClassificationSubjectKind.Incident,
            SubjectId = subjectId,
            ClassifierId = "test-classifier",
            Category = "vulnerability-scanner",
            Confidence = confidence,
            Severity = severity,
            Reasons = ["test classification"],
            RecommendedAction = "temp-ban-ip",
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static Incident Incident(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        return new Incident
        {
            Id = Guid.NewGuid(),
            CorrelationKey = $"ip={ip}",
            WindowStart = now.AddMinutes(-5),
            WindowEnd = now.AddMinutes(-1),
            EventIds = [],
            Evidence = [],
            State = IncidentState.Classified,
        };
    }

    private sealed record PolicyFixture(
        DefaultPolicyEngine Engine,
        InMemoryGuardrailStateStore GuardrailState,
        InMemoryEventStore EventStore,
        Guid IncidentId);
}
