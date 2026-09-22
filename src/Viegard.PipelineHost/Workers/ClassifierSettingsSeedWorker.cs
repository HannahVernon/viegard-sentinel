using Microsoft.Extensions.Options;
using Viegard.Application.Classifiers;

namespace Viegard.PipelineHost.Workers;

public sealed class ClassifierSettingsSeedWorker(
    IClassifierSettingsStore settingsStore,
    IOptions<ClassifierOptions> options,
    TimeProvider timeProvider,
    ILogger<ClassifierSettingsSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await settingsStore
                .TryCreateAsync(ClassifierSettings.FromOptions(options.Value, timeProvider.GetUtcNow()), stoppingToken)
                .ConfigureAwait(false);
            if (result.Created)
            {
                logger.LogInformation(
                    "Classifier settings seeded from environment configuration. ScoreForFullConfidence={ScoreForFullConfidence}; SeverityPerScorePoint={SeverityPerScorePoint}; BlockRecommendationScore={BlockRecommendationScore}; RepeatConfidenceMinEvents={RepeatConfidenceMinEvents}; RepeatConfidenceCoefficient={RepeatConfidenceCoefficient}; RepeatConfidenceBonusCap={RepeatConfidenceBonusCap}.",
                    result.Settings.ScoreForFullConfidence,
                    result.Settings.SeverityPerScorePoint,
                    result.Settings.BlockRecommendationScore,
                    result.Settings.RepeatConfidenceMinEvents,
                    result.Settings.RepeatConfidenceCoefficient,
                    result.Settings.RepeatConfidenceBonusCap);
            }
            else
            {
                logger.LogInformation("Classifier settings already exist; seed skipped.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Classifier setting seeding failed; classifier workers will use their last loaded setting set or environment fallbacks.");
        }
    }
}
