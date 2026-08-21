using Microsoft.Extensions.Options;
using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Policy;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
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
                    Id = Guid.NewGuid(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Stage = PipelineStage.Policy,
                    Summary = $"Policy produced {decision.Outcome} for classification {classification.Id}.",
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
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Policy worker failed while processing a classification lease.");
                if (lease is not null)
                {
                    try
                    {
                        await lease.AbandonAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception abandonEx)
                    {
                        logger.LogError(abandonEx, "Policy worker failed to abandon a classification lease.");
                    }
                }
            }
        }

        logger.LogInformation("Policy worker stopping.");
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
            decision.Outcome,
            GuardrailCount = decision.Guardrails.Count,
            FailedGuardrails = decision.Guardrails
                .Where(g => !g.Passed)
                .Select(g => g.GuardrailName)
                .ToList(),
        });
}
