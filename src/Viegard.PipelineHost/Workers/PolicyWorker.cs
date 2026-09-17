using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Application.Audit;
using Viegard.Application.Policy;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
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
    IAuditLedger auditLedger,
    IOptions<PolicyOptions> options,
    ILogger<PolicyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policyOptions = options.Value;
        logger.LogInformation(
            "Policy worker started for queue '{QueueName}' with policy {PolicyId} {PolicyVersion}.  DryRun={DryRun}; ManualApprovalMode={ManualApprovalMode}; EmergencyStop={EmergencyStop}; action threshold confidence {ActionConfidence}, review confidence {ReviewConfidence}, minimum severity {MinSeverity}.",
            classificationQueue.QueueName,
            DefaultPolicyEngine.DefaultPolicyId,
            DefaultPolicyEngine.DefaultPolicyVersion,
            policyOptions.Posture.DryRun,
            policyOptions.Posture.ManualApprovalMode,
            policyOptions.Posture.EmergencyStop,
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

                var posture = options.Value.Posture;
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
                await MarkIncidentDecidedAsync(classification, stoppingToken).ConfigureAwait(false);
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
            .UpsertAsync(incident with { State = IncidentState.Decided }, cancellationToken)
            .ConfigureAwait(false);
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