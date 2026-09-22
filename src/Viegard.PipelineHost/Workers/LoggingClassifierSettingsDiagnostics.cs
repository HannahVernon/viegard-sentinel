using Viegard.Application.Classifiers;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingClassifierSettingsDiagnostics(
    ILogger<LoggingClassifierSettingsDiagnostics> logger) : IClassifierSettingsDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(
            exception,
            "Classifier settings refresh failed; retaining the last known good setting set. If no settings row has loaded yet, classification uses the environment-configured classifier settings.");
    }
}
