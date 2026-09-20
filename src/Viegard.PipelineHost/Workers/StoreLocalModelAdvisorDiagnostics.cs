using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class StoreLocalModelAdvisorDiagnostics(
    ILocalModelAdvisorConsultStore consultStore,
    ILogger<StoreLocalModelAdvisorDiagnostics> logger) : ILocalModelAdvisorDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(exception, "Local-model advisor settings refresh failed; using the last-known-good snapshot or disabled fallback.");
    }

    public async Task RecordConsultAsync(AdvisorConsultRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            await consultStore.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Local-model advisor consult recording failed for classification {ClassificationId}.", record.ClassificationId);
        }
    }
}
