using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Classifiers;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Incidents;

namespace Viegard.PipelineHost.Workers;

public sealed class ClassificationWorker(
    IWorkQueue<IncidentWorkItem> incidentQueue,
    IWorkQueue<ClassificationWorkItem> classificationQueue,
    IIncidentStore incidentStore,
    IEnumerable<IClassifier> classifiers,
    IClassificationStore classificationStore,
    IAuditLedger auditLedger,
    ILogger<ClassificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var classifierList = classifiers.ToList();
        logger.LogInformation(
            "Classification worker started for queue '{QueueName}' with {ClassifierCount} classifier(s).",
            incidentQueue.QueueName,
            classifierList.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            IWorkLease<IncidentWorkItem>? lease = null;
            try
            {
                lease = await incidentQueue.LeaseAsync(stoppingToken).ConfigureAwait(false);
                var incidentId = lease.Message.IncidentId;
                var incident = await incidentStore.GetAsync(incidentId, stoppingToken).ConfigureAwait(false);
                if (incident is null)
                {
                    logger.LogWarning("Classification worker could not find incident {IncidentId}; completing lease.", incidentId);
                    await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (incident.State != IncidentState.Open)
                {
                    logger.LogInformation(
                        "Classification worker skipped incident {IncidentId} because state is {State}.",
                        incident.Id,
                        incident.State);
                    await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (incident.Evidence.Count == 0)
                {
                    logger.LogInformation(
                        "Classification worker skipped incident {IncidentId} because it has no evidence.",
                        incident.Id);
                    await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var subject = new ClassificationSubject
                {
                    Kind = ClassificationSubjectKind.Incident,
                    SubjectId = incident.Id,
                };
                var succeeded = 0;

                foreach (var classifier in classifierList)
                {
                    var outcome = await classifier.ClassifyAsync(subject, stoppingToken).ConfigureAwait(false);
                    if (!outcome.Succeeded || outcome.Classification is null)
                    {
                        await auditLedger.AppendAsync(new AuditRecord
                        {
                            Id = Guid.NewGuid(),
                            Timestamp = DateTimeOffset.UtcNow,
                            Stage = PipelineStage.Classification,
                            Summary = $"Classification failed for incident {incident.Id} with classifier {classifier.ClassifierId}.",
                            IncidentId = incident.Id,
                            DetailJson = JsonSerializer.Serialize(new
                            {
                                classifier.ClassifierId,
                                outcome.FailureKind,
                                outcome.FailureDetail,
                            }),
                        }, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var classification = outcome.Classification;
                    await classificationStore.AddAsync(classification, stoppingToken).ConfigureAwait(false);
                    await classificationQueue
                        .EnqueueAsync(new ClassificationWorkItem(classification.Id), stoppingToken)
                        .ConfigureAwait(false);
                    await auditLedger.AppendAsync(new AuditRecord
                    {
                        Id = Guid.NewGuid(),
                        Timestamp = DateTimeOffset.UtcNow,
                        Stage = PipelineStage.Classification,
                        Summary = $"Classifier {classification.ClassifierId} produced {classification.Category} for incident {incident.Id}.",
                        IncidentId = incident.Id,
                        ClassificationId = classification.Id,
                        DetailJson = ClassificationDetailJson(classification),
                    }, stoppingToken).ConfigureAwait(false);
                    succeeded++;
                }

                if (succeeded > 0)
                {
                    await incidentStore
                        .UpsertAsync(incident with { State = IncidentState.Classified }, stoppingToken)
                        .ConfigureAwait(false);
                }

                await lease.CompleteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Classification worker failed while processing an incident lease.");
                if (lease is not null)
                {
                    try
                    {
                        await lease.AbandonAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception abandonEx)
                    {
                        logger.LogError(abandonEx, "Classification worker failed to abandon an incident lease.");
                    }
                }
            }
        }

        logger.LogInformation("Classification worker stopping.");
    }

    private static string ClassificationDetailJson(Classification classification) =>
        JsonSerializer.Serialize(new
        {
            classification.ClassifierId,
            classification.Category,
            classification.Confidence,
            classification.Severity,
            classification.RecommendedAction,
            Reasons = classification.Reasons.Take(10).ToList(),
        });
}
