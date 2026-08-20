using Microsoft.Extensions.Options;
using Viegard.Application.Stores;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
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
        else if (TryMatchAllowlist(classification, policyOptions, out var allowlistDetail))
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
            Id = Guid.NewGuid(),
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

    private static bool TryMatchAllowlist(
        Classification classification,
        PolicyOptions options,
        out string detail)
    {
        if (classification.SubjectKind != ClassificationSubjectKind.MailMessage)
        {
            detail = "Not a mail-message subject; allowlist guardrail skipped.";
            return false;
        }

        if (!classification.Category.Contains("sender", StringComparison.OrdinalIgnoreCase))
        {
            detail = "Mail-message category is not a mail-sender category; allowlist guardrail skipped.";
            return false;
        }

        if (options.AllowedSenders.Count == 0 && options.AllowedDomains.Count == 0)
        {
            detail = "Mail-sender category evaluated, but no allowed senders or domains are configured.";
            return false;
        }

        if (!TryExtractSender(classification, out var sender))
        {
            detail = "Mail-sender category evaluated, but no sender value was present in classification details.";
            return false;
        }

        var normalizedSender = sender.ToLowerInvariant();
        var senderMatched = options.AllowedSenders
            .Select(static s => s.Trim().ToLowerInvariant())
            .Any(s => s.Length > 0 && s == normalizedSender);
        if (senderMatched)
        {
            detail = $"Sender {sender} is allowlisted.";
            return true;
        }

        var at = normalizedSender.LastIndexOf('@');
        var domain = at >= 0 ? normalizedSender[(at + 1)..] : normalizedSender;
        var domainMatched = options.AllowedDomains
            .Select(static d => d.Trim().TrimStart('@').ToLowerInvariant())
            .Any(d => d.Length > 0 && (domain == d || domain.EndsWith($".{d}", StringComparison.Ordinal)));

        detail = domainMatched
            ? $"Sender domain {domain} is allowlisted."
            : $"Sender {sender} did not match configured allowlists.";
        return domainMatched;
    }

    private static bool TryExtractSender(Classification classification, out string sender)
    {
        foreach (var candidate in ClassificationTextCandidates(classification))
        {
            if (TryExtractValue(candidate, "sender=", out sender)
                || TryExtractValue(candidate, "sender:", out sender)
                || TryExtractValue(candidate, "from=", out sender)
                || TryExtractValue(candidate, "from:", out sender))
            {
                return true;
            }
        }

        sender = string.Empty;
        return false;
    }

    private static IEnumerable<string> ClassificationTextCandidates(Classification classification)
    {
        yield return classification.Category;
        if (!string.IsNullOrWhiteSpace(classification.RecommendedAction))
        {
            yield return classification.RecommendedAction;
        }

        foreach (var reason in classification.Reasons)
        {
            yield return reason;
        }
    }

    private static bool TryExtractValue(string text, string marker, out string value)
    {
        value = string.Empty;
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        while (start < text.Length && IsTrimCharacter(text[start]))
        {
            start++;
        }

        var end = start;
        while (end < text.Length && !IsTerminator(text[end]))
        {
            end++;
        }

        value = text[start..end].Trim().Trim('<', '>', '"', '\'');
        return value.Length > 0;
    }

    private static bool IsTrimCharacter(char c) =>
        char.IsWhiteSpace(c) || c is '<' or '"' or '\'';

    private static bool IsTerminator(char c) =>
        char.IsWhiteSpace(c) || c is ',' or ';' or ')' or ']' or '>' or '"' or '\'';

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
