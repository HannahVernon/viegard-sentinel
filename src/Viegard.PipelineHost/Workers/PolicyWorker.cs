using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Actions.MikroTik;
using Viegard.Application.Actions;
using Viegard.Application.Audit;
using Viegard.Application.Policy;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Incidents;

namespace Viegard.PipelineHost.Workers;

public sealed class PolicyWorker(
    IWorkQueue<ClassificationWorkItem> classificationQueue,
    IClassificationStore classificationStore,
    IDecisionStore decisionStore,
    IIncidentStore incidentStore,
    IPolicyEngine policyEngine,
    IActionStore actionStore,
    IWorkQueue<ActionWorkItem> actionQueue,
    IGuardrailStateStore guardrailState,
    IAuditLedger auditLedger,
    IOptions<PolicyOptions> options,
    PolicyPostureSource postureSource,
    ILogger<PolicyWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policyOptions = options.Value;
        var startupPosture = postureSource.CurrentValues(policyOptions);
        logger.LogInformation(
            "Policy worker started for queue '{QueueName}' with policy {PolicyId} {PolicyVersion}.  DryRun={DryRun}; ManualApprovalMode={ManualApprovalMode}; EmergencyStop={EmergencyStop}; action threshold confidence {ActionConfidence}, review confidence {ReviewConfidence}, minimum severity {MinSeverity}.  Posture is database-owned once seeded; values shown are the current snapshot.",
            classificationQueue.QueueName,
            DefaultPolicyEngine.DefaultPolicyId,
            DefaultPolicyEngine.DefaultPolicyVersion,
            startupPosture.DryRun,
            startupPosture.ManualApprovalMode,
            startupPosture.EmergencyStop,
            policyOptions.AiActionConfidence,
            policyOptions.AiReviewConfidence,
            policyOptions.AiActionMinSeverity);

        while (!stoppingToken.IsCancellationRequested)
        {
            IWorkLease<ClassificationWorkItem>? lease = null;
            try
            {
                lease = await classificationQueue.LeaseAsync(stoppingToken).ConfigureAwait(false);
                var classificationId = lease.Message.ClassificationId;
                var classification = await classificationStore.GetAsync(classificationId, stoppingToken).ConfigureAwait(false);
                if (classification is null)
                {
                    logger.LogWarning(
                        "Policy worker could not find classification {ClassificationId}; completing lease.",
                        classificationId);
                    await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var posture = postureSource.CurrentValues(options.Value);
                var decision = await policyEngine
                    .EvaluateAsync(
                        classification,
                        new PolicyContext
                        {
                            DryRun = posture.DryRun,
                            ManualApprovalMode = posture.ManualApprovalMode,
                            EmergencyStop = posture.EmergencyStop,
                        },
                        stoppingToken)
                    .ConfigureAwait(false);

                await decisionStore.AddAsync(decision, stoppingToken).ConfigureAwait(false);
                await MarkIncidentDecidedAsync(classification, decision, stoppingToken).ConfigureAwait(false);
                await auditLedger.AppendAsync(new AuditRecord
                {
                    Id = ViegardId.New(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Stage = PipelineStage.Policy,
                    Summary = $"Policy produced {OutcomeLabel(decision.Outcome)} for classification {classification.Id}.",
                    IncidentId = classification.SubjectKind == ClassificationSubjectKind.Incident
                        ? classification.SubjectId
                        : null,
                    ClassificationId = classification.Id,
                    DecisionId = decision.Id,
                    DetailJson = DecisionDetailJson(decision),
                }, stoppingToken).ConfigureAwait(false);

                await DispatchAuthorizedActionAsync(decision, stoppingToken).ConfigureAwait(false);

                await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (lease is not null)
                {
                    await AbandonLeaseAsync(lease, chargeAttempt: false).ConfigureAwait(false);
                }

                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Policy worker failed while processing a classification lease.");
                if (lease is not null)
                {
                    await AbandonLeaseAsync(lease, chargeAttempt: true).ConfigureAwait(false);
                }
            }
        }

        logger.LogInformation("Policy worker stopping.");
    }

    /// <summary>
    /// The missing last mile of the unattended tier (found live: the first
    /// ActionAuthorized decision passed every guardrail and then nothing
    /// happened, because only the manual-approval endpoint dispatched
    /// actions).  Mirrors the approval flow's construction; the MikroTik
    /// provider still re-checks the dry-run posture at execution time.
    /// </summary>
    private async Task DispatchAuthorizedActionAsync(
        Viegard.Domain.Decisions.Decision decision,
        CancellationToken cancellationToken)
    {
        if (decision.Outcome != Viegard.Domain.Decisions.DecisionOutcome.ActionAuthorized)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(decision.AuthorizedTargetIp))
        {
            logger.LogError(
                "Decision {DecisionId} is ActionAuthorized but carries no target IP; nothing dispatched.",
                decision.Id);
            return;
        }

        var duration = decision.RecommendedActionDuration ?? options.Value.TempBanDuration;
        var timeout = MikroTikBanActionProvider.FormatRouterOsDuration(duration);
        var action = new ActionRecord
        {
            Id = ViegardId.New(),
            DecisionId = decision.Id,
            ProviderId = MikroTikBanActionProvider.MikroTikProviderId,
            OperationId = MikroTikBanActionProvider.BanIpOperationId,
            ParametersJson = JsonSerializer.Serialize(
                new BanParameters(decision.AuthorizedTargetIp, timeout), Json),
            Status = ActionStatus.Pending,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        await actionStore.AddAsync(action, cancellationToken).ConfigureAwait(false);
        await actionQueue.EnqueueAsync(new ActionWorkItem(action.Id), cancellationToken).ConfigureAwait(false);
        // Feed the rate-cap counters the engine's rate-caps guardrail reads;
        // without this the hourly/daily caps count nothing.
        await guardrailState.RecordAutoActionAsync(action.RequestedAt, cancellationToken).ConfigureAwait(false);
        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = ViegardId.New(),
            Timestamp = DateTimeOffset.UtcNow,
            Stage = PipelineStage.Policy,
            Summary = $"Automatic ban action {action.Id:N} queued for {decision.AuthorizedTargetIp} ({timeout}).",
            DecisionId = decision.Id,
            ActionId = action.Id,
            DetailJson = JsonSerializer.Serialize(new
            {
                Kind = "AutomaticActionDispatched",
                decision.AuthorizedTargetIp,
                Timeout = timeout,
                ProviderId = MikroTikBanActionProvider.MikroTikProviderId,
                OperationId = MikroTikBanActionProvider.BanIpOperationId,
            }, Json),
        }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Automatic ban action {ActionId} queued for {TargetIp} ({Timeout}) from decision {DecisionId}.",
            action.Id,
            decision.AuthorizedTargetIp,
            timeout,
            decision.Id);
    }

    private sealed record BanParameters(string Ip, string Timeout);

    private async Task AbandonLeaseAsync(IWorkLease<ClassificationWorkItem> lease, bool chargeAttempt)
    {
        try
        {
            await lease.AbandonAsync(chargeAttempt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception abandonEx)
        {
            logger.LogError(
                abandonEx,
                chargeAttempt
                    ? "Policy worker failed to abandon a classification lease."
                    : "Policy worker failed to release a classification lease during shutdown.");
        }
    }

    private async Task MarkIncidentDecidedAsync(
        Classification classification,
        Viegard.Domain.Decisions.Decision decision,
        CancellationToken cancellationToken)
    {
        if (classification.SubjectKind != ClassificationSubjectKind.Incident)
        {
            return;
        }

        var incident = await incidentStore.GetAsync(classification.SubjectId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            logger.LogWarning(
                "Policy worker could not mark incident {IncidentId} decided because it was not found.",
                classification.SubjectId);
            return;
        }

        await incidentStore
            .UpsertAsync(
                incident with
                {
                    State = IncidentState.Decided,
                    DecidedEventCount = incident.EventIds.Count,
                },
                cancellationToken)
            .ConfigureAwait(false);

        await SupersedeProvisionalDecisionsAsync(incident.Id, classification.Id, decision, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// When a merged decision is produced for a coalesced incident, withdraw the
    /// earlier provisional decision(s) from a prior classification pass that are
    /// still pending review, so <c>/decisions</c> shows one actionable decision.
    /// A decision a human already actioned is never withdrawn.
    /// </summary>
    private async Task SupersedeProvisionalDecisionsAsync(
        Guid incidentId,
        Guid currentClassificationId,
        Viegard.Domain.Decisions.Decision decision,
        CancellationToken cancellationToken)
    {
        var classifications = await classificationStore
            .ListForSubjectAsync(ClassificationSubjectKind.Incident, incidentId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var priorClassification in classifications)
        {
            if (priorClassification.Id == currentClassificationId)
            {
                continue;
            }

            var priorDecisions = await decisionStore
                .ListForClassificationAsync(priorClassification.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (var prior in priorDecisions)
            {
                if (prior.Id == decision.Id
                    || prior.Outcome != Viegard.Domain.Decisions.DecisionOutcome.RequireApproval
                    || prior.ReviewedAt is not null
                    || prior.SupersededAt is not null)
                {
                    continue;
                }

                var superseded = await decisionStore
                    .TrySupersedeAsync(prior.Id, decision.Id, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
                if (superseded is null)
                {
                    continue;
                }

                await auditLedger.AppendAsync(new AuditRecord
                {
                    Id = ViegardId.New(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Stage = PipelineStage.Policy,
                    Summary = $"Provisional decision {prior.Id} superseded by merged decision {decision.Id} for incident {incidentId}.",
                    IncidentId = incidentId,
                    ClassificationId = priorClassification.Id,
                    DecisionId = prior.Id,
                    DetailJson = JsonSerializer.Serialize(new
                    {
                        Kind = "DecisionSuperseded",
                        SupersededByDecisionId = decision.Id,
                    }, Json),
                }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string DecisionDetailJson(Viegard.Domain.Decisions.Decision decision) =>
        JsonSerializer.Serialize(new
        {
            decision.PolicyId,
            decision.PolicyVersion,
            Outcome = OutcomeLabel(decision.Outcome),
            GuardrailCount = decision.Guardrails.Count,
            FailedGuardrails = decision.Guardrails
                .Where(g => !g.Passed)
                .Select(g => g.GuardrailName)
                .ToList(),
        });

    private static string OutcomeLabel(Viegard.Domain.Decisions.DecisionOutcome outcome) => outcome switch
    {
        Viegard.Domain.Decisions.DecisionOutcome.ActionAuthorized => "Action authorized",
        Viegard.Domain.Decisions.DecisionOutcome.RecordOnly => "Record only",
        Viegard.Domain.Decisions.DecisionOutcome.RequireApproval => "Require approval",
        Viegard.Domain.Decisions.DecisionOutcome.DryRun => "Dry run",
        _ => outcome.ToString(),
    };
}