using Viegard.Application.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingLocalModelAdvisorDiagnostics(ILogger<LoggingLocalModelAdvisorDiagnostics> logger) : ILocalModelAdvisorDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(exception, "Local-model advisor settings refresh failed; using the last-known-good snapshot or disabled fallback.");
    }

    public Task RecordConsultAsync(AdvisorConsultRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
