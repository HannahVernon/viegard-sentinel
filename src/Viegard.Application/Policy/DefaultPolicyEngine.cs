using Microsoft.Extensions.Options;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Policy;

public static class PolicyGuardrailNames
{
    public const string EmergencyStop = "emergency-stop";
    public const string ProtectedAddress = "protected-address";
    public const string Allowlist = "allowlist";
    public const string Thresholds = "thresholds";
    public const string RepeatOffender = "repeat-offender";
    public const string RateCaps = "rate-caps";
    public const string CircuitBreaker = "circuit-breaker";
    public const string PostureOverlay = "posture-overlay";
}

public sealed class DefaultPolicyEngine(
    IOptions<PolicyOptions> options,
    ProtectedAddressList protectedAddresses,
    IIncidentStore incidentStore,
    IEventStore eventStore,
    IGuardrailStateStore guardrailStateStore) : IPolicyEngine
{
    public const string DefaultPolicyId = "viegard-default";
    public const string DefaultPolicyVersion = "0.1-provisional";

    public async Task<Decision> EvaluateAsync(
        Classification classification,
        PolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(context);

        var policyOptions = options.Value;
        var guardrails = new List<GuardrailEvaluation>();
        var rationale = new List<string>();
        var outcome = DecisionOutcome.Permit;
        var denied = false;
        string? targetIp = null;
        Incident? targetIncident = null;
        var now = DateTimeOffset.UtcNow;
        var recommendedDuration = policyOptions.TempBanDuration;

        if (context.EmergencyStop)
        {
            guardrails.Add(Fail(PolicyGuardrailNames.EmergencyStop, "Emergency stop is engaged."));
            rationale.Add("Emergency stop engaged; automated action denied.");
            outcome = DecisionOutcome.Deny;
            denied = true;
        }
        else
        {
            guardrails.Add(Pass(PolicyGuardrailNames.EmergencyStop, "Emergency stop is not engaged."));
        }

        if (denied)
        {
            guardrails.Add(Skipped(PolicyGuardrailNames.ProtectedAddress, "Skipped because emergency stop already denied action."));
        }
        else
        {
            var target = await ResolveTargetIpAsync(classification, cancellationToken).ConfigureAwait(false);
            targetIp = target.Ip;
            targetIncident = target.Incident;

            if (target.Kind == TargetIpKind.NotApplicable)
            {
                guardrails.Add(Pass(PolicyGuardrailNames.ProtectedAddress, target.Detail));
            }
            else if (targetIp is null || protectedAddresses.IsProtected(targetIp))
            {
                guardrails.Add(Fail(
                    PolicyGuardrailNames.ProtectedAddress,
                    targetIp is null
                        ? $"{target.Detail}  Treating target as protected fail-closed."
                        : $"Target IP {targetIp} is protected or unparseable."));
                rationale.Add("Protected-address guardrail denied automated action.");
                outcome = DecisionOutcome.Deny;
                denied = true;
            }
            else
            {
                guardrails.Add(Pass(PolicyGuardrailNames.ProtectedAddress, $"Target IP {targetIp} is not protected."));
            }
        }

        if (denied)
        {
            guardrails.Add(Skipped(PolicyGuardrailNames.Allowlist, "Skipped because action is already denied."));
        }
        else
        {
            var (allowlistMatched, allowlistDetail) = await MatchAllowlistAsync(classification, policyOptions, cancellationToken).ConfigureAwait(false);
            if (allowlistMatched)
            {
                guardrails.Add(Fail(PolicyGuardrailNames.Allowlist, allowlistDetail));
                rationale.Add("Allowlist guardrail denied automated action.");
                outcome = DecisionOutcome.Deny;
                denied = true;
            }
            else
            {
                guardrails.Add(Pass(PolicyGuardrailNames.Allowlist, allowlistDetail));
            }
        }

        if (denied)
        {
            guardrails.Add(Skipped(PolicyGuardrailNames.Thresholds, "Skipped because action is already denied."));
        }
        else
        {
            var threshold = EvaluateThresholds(classification, policyOptions);
            outcome = threshold.Outcome;
            guardrails.Add(new GuardrailEvaluation
            {
                GuardrailName = PolicyGuardrailNames.Thresholds,
                Passed = threshold.Passed,
                Detail = threshold.Detail,
            });
            rationale.Add(threshold.Rationale);
            denied = outcome == DecisionOutcome.Deny;
        }

        if (denied || outcome != DecisionOutcome.Permit || targetIp is null)
        {
            guardrails.Add(Skipped(
                PolicyGuardrailNames.RepeatOffender,
                denied
                    ? "Skipped because action is denied."
                    : "Skipped because no automatic IP action is currently permitted."));
        }
        else
        {
            var incidentTimestamp = targetIncident is null
                ? now
                : targetIncident.WindowEnd <= now ? targetIncident.WindowEnd : now;
            if (targetIncident is not null)
            {
                await guardrailStateStore
                    .RecordIncidentAsync(targetIp, targetIncident.Id, incidentTimestamp, cancellationToken)
                    .ConfigureAwait(false);
            }

            var incidentCount = await guardrailStateStore
                .GetIncidentCountAsync(targetIp, now.Subtract(policyOptions.RepeatOffenderWindow), now, cancellationToken)
                .ConfigureAwait(false);
            var repeatOffender = incidentCount >= policyOptions.RepeatOffenderIncidentCount;
            recommendedDuration = repeatOffender
                ? policyOptions.RepeatOffenderBanDuration
                : policyOptions.TempBanDuration;
            if (recommendedDuration > policyOptions.MaxAutoBanDuration)
            {
                recommendedDuration = policyOptions.MaxAutoBanDuration;
            }

            var repeatDetail = repeatOffender
                ? $"Repeat-offender lookback matched {incidentCount} incident(s) within {FormatDuration(policyOptions.RepeatOffenderWindow)}; recommended duration {FormatDuration(recommendedDuration)}."
                : $"Repeat-offender lookback found {incidentCount} incident(s) within {FormatDuration(policyOptions.RepeatOffenderWindow)}; recommended duration {FormatDuration(recommendedDuration)}.";
            guardrails.Add(Pass(PolicyGuardrailNames.RepeatOffender, repeatDetail));
            rationale.Add(repeatDetail);
        }

        var rateCapBreached = false;
        if (denied || outcome != DecisionOutcome.Permit)
        {
            guardrails.Add(Skipped(
                PolicyGuardrailNames.RateCaps,
                denied
                    ? "Skipped because action is denied."
                    : "Skipped because no automatic action is currently permitted."));
        }
        else
        {
            var counts = await guardrailStateStore.GetAutoActionCountsAsync(now, cancellationToken).ConfigureAwait(false);
            rateCapBreached = counts.LastHour >= policyOptions.MaxAutoActionsPerHour
                || counts.LastDay >= policyOptions.MaxAutoActionsPerDay;
            if (rateCapBreached)
            {
                await guardrailStateStore.SetCircuitBreakerOpenAsync(true, cancellationToken).ConfigureAwait(false);
                guardrails.Add(Fail(
                    PolicyGuardrailNames.RateCaps,
                    $"Auto-action cap reached: {counts.LastHour}/{policyOptions.MaxAutoActionsPerHour} in the last hour, {counts.LastDay}/{policyOptions.MaxAutoActionsPerDay} in the last day."));
                rationale.Add("Rate cap reached; manual approval required.");
                outcome = DecisionOutcome.RequireApproval;
            }
            else
            {
                guardrails.Add(Pass(
                    PolicyGuardrailNames.RateCaps,
                    $"Auto-action counts are {counts.LastHour}/{policyOptions.MaxAutoActionsPerHour} in the last hour and {counts.LastDay}/{policyOptions.MaxAutoActionsPerDay} in the last day."));
            }
        }

        if (denied)
        {
            guardrails.Add(Skipped(PolicyGuardrailNames.CircuitBreaker, "Skipped because action is denied."));
        }
        else
        {
            var consecutiveFailures = await guardrailStateStore
                .GetConsecutiveActionFailuresAsync(cancellationToken)
                .ConfigureAwait(false);
            var circuitOpen = await guardrailStateStore.IsCircuitBreakerOpenAsync(cancellationToken).ConfigureAwait(false);
            if (consecutiveFailures >= policyOptions.CircuitBreakerFailureThreshold)
            {
                await guardrailStateStore.SetCircuitBreakerOpenAsync(true, cancellationToken).ConfigureAwait(false);
                circuitOpen = true;
            }

            if (circuitOpen)
            {
                guardrails.Add(Fail(
                    PolicyGuardrailNames.CircuitBreaker,
                    rateCapBreached
                        ? "Circuit breaker is open after a rate-cap breach."
                        : $"Circuit breaker is open; consecutive action failures: {consecutiveFailures}."));
                rationale.Add("Circuit breaker open; manual approval required.");
                outcome = DecisionOutcome.RequireApproval;
            }
            else
            {
                guardrails.Add(Pass(
                    PolicyGuardrailNames.CircuitBreaker,
                    $"Circuit breaker is closed; consecutive action failures: {consecutiveFailures}/{policyOptions.CircuitBreakerFailureThreshold}."));
            }
        }

        if (outcome == DecisionOutcome.Deny)
        {
            guardrails.Add(Pass(PolicyGuardrailNames.PostureOverlay, "No posture overlay applied because action is denied."));
        }
        else
        {
            var beforeOverlay = outcome;
            if (context.ManualApprovalMode)
            {
                outcome = DecisionOutcome.RequireApproval;
            }

            if (context.DryRun)
            {
                outcome = DecisionOutcome.DryRun;
            }

            if (outcome == beforeOverlay)
            {
                guardrails.Add(Pass(PolicyGuardrailNames.PostureOverlay, "No posture overlay changed the decision."));
            }
            else
            {
                guardrails.Add(Fail(
                    PolicyGuardrailNames.PostureOverlay,
                    $"Posture overlay changed {beforeOverlay} to {outcome}.  ManualApprovalMode={context.ManualApprovalMode}; DryRun={context.DryRun}."));
                rationale.Add($"Posture overlay changed decision from {beforeOverlay} to {outcome}.");
            }
        }

        if (outcome is DecisionOutcome.Permit or DecisionOutcome.DryRun)
        {
            rationale.Add($"Recommended action duration: {FormatDuration(recommendedDuration)}.");
        }

        return new Decision
        {
            Id = ViegardId.New(),
            ClassificationId = classification.Id,
            PolicyId = DefaultPolicyId,
            PolicyVersion = DefaultPolicyVersion,
            Outcome = outcome,
            Rationale = string.Join(' ', rationale),
            Guardrails = guardrails,
            CreatedAt = now,
        };
    }

    private async ValueTask<TargetIpResult> ResolveTargetIpAsync(
        Classification classification,
        CancellationToken cancellationToken)
    {
        if (classification.SubjectKind == ClassificationSubjectKind.MailMessage)
        {
            return TargetIpResult.NotApplicable("Mail-message subjects have no policy target IP; protected-address guardrail skipped.");
        }

        var incident = await incidentStore.GetAsync(classification.SubjectId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return TargetIpResult.Unavailable($"Incident {classification.SubjectId} was not found.");
        }

        const string prefix = "ip=";
        if (!incident.CorrelationKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return TargetIpResult.Unavailable(
                $"Incident {incident.Id} correlation key does not start with the expected '{prefix}' prefix.");
        }

        var end = incident.CorrelationKey.IndexOf('|', prefix.Length);
        var ip = end < 0
            ? incident.CorrelationKey[prefix.Length..].Trim()
            : incident.CorrelationKey[prefix.Length..end].Trim();

        return string.IsNullOrWhiteSpace(ip)
            ? TargetIpResult.Unavailable($"Incident {incident.Id} correlation key has an empty IP value.")
            : TargetIpResult.Found(ip, incident);
    }

    private static ThresholdDecision EvaluateThresholds(Classification classification, PolicyOptions options)
    {
        if (classification.Model is not null)
        {
            if (classification.Confidence >= options.AiActionConfidence
                && classification.Severity >= options.AiActionMinSeverity)
            {
                return new ThresholdDecision(
                    DecisionOutcome.Permit,
                    true,
                    $"AI gate passed: confidence {classification.Confidence:0.###} >= {options.AiActionConfidence:0.###} and severity {classification.Severity} >= {options.AiActionMinSeverity}.",
                    "AI classification met the automatic-action band.");
            }

            if (classification.Confidence >= options.AiReviewConfidence
                && classification.Severity >= options.AiActionMinSeverity)
            {
                return new ThresholdDecision(
                    DecisionOutcome.RequireApproval,
                    true,
                    $"AI gate is in the review band: confidence {classification.Confidence:0.###}, severity {classification.Severity}.",
                    "AI classification is flagged for review.");
            }

            return new ThresholdDecision(
                DecisionOutcome.Deny,
                false,
                $"AI gate is below the review band: confidence {classification.Confidence:0.###}, severity {classification.Severity}.",
                "AI classification is record-only.");
        }

        if (classification.Confidence >= options.AiActionConfidence)
        {
            return new ThresholdDecision(
                DecisionOutcome.Permit,
                true,
                $"Deterministic gate passed: normalized evidence confidence {classification.Confidence:0.###} >= {options.AiActionConfidence:0.###}.  Raw evidence threshold {options.EvidenceScoreBlockThreshold:0.###} is applied upstream by detection.",
                $"Deterministic classification met the automatic-action band; raw evidence threshold {options.EvidenceScoreBlockThreshold:0.###} is applied upstream.");
        }

        if (classification.Confidence >= options.AiReviewConfidence)
        {
            return new ThresholdDecision(
                DecisionOutcome.RequireApproval,
                true,
                $"Deterministic gate is in the review band: normalized evidence confidence {classification.Confidence:0.###}.  Raw evidence threshold {options.EvidenceScoreBlockThreshold:0.###} is applied upstream by detection.",
                $"Deterministic classification is flagged for review; raw evidence threshold {options.EvidenceScoreBlockThreshold:0.###} is applied upstream.");
        }

        return new ThresholdDecision(
            DecisionOutcome.Deny,
            false,
            $"Deterministic gate is below the review band: normalized evidence confidence {classification.Confidence:0.###}.",
            "Deterministic classification is record-only.");
    }

    /// <summary>
    /// Checks whether the classified mail message's structured From
    /// addresses match the configured sender/domain allowlists.  The sender
    /// is read exclusively from the stored MailMessageEvent; classification
    /// text (category, reasons, recommended action) is never parsed for
    /// sender values because that text derives from attacker-influenceable
    /// mail content (security-audit finding, 2026-08-25).
    /// </summary>
    private async Task<(bool Matched, string Detail)> MatchAllowlistAsync(
        Classification classification,
        PolicyOptions options,
        CancellationToken cancellationToken)
    {
        if (classification.SubjectKind != ClassificationSubjectKind.MailMessage)
        {
            return (false, "Not a mail-message subject; allowlist guardrail skipped.");
        }

        if (options.AllowedSenders.Count == 0 && options.AllowedDomains.Count == 0)
        {
            return (false, "Mail-message subject evaluated, but no allowed senders or domains are configured.");
        }

        var mailEvent = await eventStore.GetAsync(classification.SubjectId, cancellationToken).ConfigureAwait(false);
        if (mailEvent?.Payload is not MailMessageEvent mail)
        {
            return (false, $"Mail event {classification.SubjectId} was not found or is not a mail payload; sender cannot be verified.");
        }

        var senders = mail.From
            .Select(static a => a.Address.Trim())
            .Where(static a => a.Length > 0)
            .ToList();
        if (senders.Count == 0)
        {
            return (false, "Mail event has no From addresses; sender cannot be verified.");
        }

        var allowedSenders = options.AllowedSenders
            .Select(static s => s.Trim().ToLowerInvariant())
            .Where(static s => s.Length > 0)
            .ToList();
        var allowedDomains = options.AllowedDomains
            .Select(static d => d.Trim().TrimStart('@').ToLowerInvariant())
            .Where(static d => d.Length > 0)
            .ToList();

        foreach (var sender in senders)
        {
            var normalizedSender = sender.ToLowerInvariant();
            if (allowedSenders.Contains(normalizedSender))
            {
                return (true, $"Sender {sender} is allowlisted.");
            }

            var at = normalizedSender.LastIndexOf('@');
            var domain = at >= 0 ? normalizedSender[(at + 1)..] : normalizedSender;
            if (allowedDomains.Any(d => domain == d || domain.EndsWith($".{d}", StringComparison.Ordinal)))
            {
                return (true, $"Sender domain {domain} is allowlisted.");
            }
        }

        return (false, $"No From address matched configured allowlists ({senders.Count} checked).");
    }

    private static GuardrailEvaluation Pass(string name, string detail) =>
        new()
        {
            GuardrailName = name,
            Passed = true,
            Detail = detail,
        };

    private static GuardrailEvaluation Fail(string name, string detail) =>
        new()
        {
            GuardrailName = name,
            Passed = false,
            Detail = detail,
        };

    private static GuardrailEvaluation Skipped(string name, string detail) =>
        Pass(name, detail);

    private static string FormatDuration(TimeSpan value) =>
        value.ToString(value.TotalDays >= 1 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");

    private enum TargetIpKind
    {
        Found,
        NotApplicable,
        Unavailable,
    }

    private sealed record TargetIpResult(TargetIpKind Kind, string? Ip, Incident? Incident, string Detail)
    {
        public static TargetIpResult Found(string ip, Incident incident) =>
            new(TargetIpKind.Found, ip, incident, $"Target IP {ip} resolved from incident {incident.Id}.");

        public static TargetIpResult NotApplicable(string detail) =>
            new(TargetIpKind.NotApplicable, null, null, detail);

        public static TargetIpResult Unavailable(string detail) =>
            new(TargetIpKind.Unavailable, null, null, detail);
    }

    private sealed record ThresholdDecision(
        DecisionOutcome Outcome,
        bool Passed,
        string Detail,
        string Rationale);
}