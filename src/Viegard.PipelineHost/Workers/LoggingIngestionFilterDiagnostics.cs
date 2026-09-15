using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingIngestionFilterDiagnostics(
    ILogger<LoggingIngestionFilterDiagnostics> logger) : IIngestionFilterDiagnostics
{
    public void InvalidFilterSkipped(IngestionFilter filter, string reason)
    {
        logger.LogWarning(
            "Skipping ingestion filter row for source type '{SourceType}' and event kind '{EventKind}': {Reason}.",
            filter.SourceType,
            filter.EventKind,
            reason);
    }

    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(
            exception,
            "Ingestion filter refresh failed; retaining the last known good filter set. If no filter set has loaded yet, all events are emitted.");
    }
}
