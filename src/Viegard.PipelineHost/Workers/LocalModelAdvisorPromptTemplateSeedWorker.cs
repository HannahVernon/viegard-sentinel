using Viegard.Application.Classifiers;
using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class LocalModelAdvisorPromptTemplateSeedWorker(
    ILocalModelAdvisorPromptTemplateStore promptTemplates,
    TimeProvider timeProvider,
    ILogger<LocalModelAdvisorPromptTemplateSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var active = await promptTemplates
                .GetActiveAsync(AdvisoryIncidentClassifier.PromptTemplateId, stoppingToken)
                .ConfigureAwait(false);
            if (active is not null)
            {
                logger.LogInformation("Local-model advisor prompt template already exists; seed skipped.");
                return;
            }

            var template = LocalModelAdvisorPrompt.Template;
            var created = await promptTemplates.CreateRevisionAsync(
                    template.TemplateId,
                    template.SystemInstructions,
                    template.ApplicationInstructions,
                    "Seeded from code template.",
                    "system",
                    timeProvider.GetUtcNow(),
                    stoppingToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Local-model advisor prompt template seeded. TemplateId={TemplateId}; Revision={Revision}.",
                created.TemplateId,
                created.Revision);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Local-model advisor prompt template seeding failed; classification will use the code template fallback.");
        }
    }
}
