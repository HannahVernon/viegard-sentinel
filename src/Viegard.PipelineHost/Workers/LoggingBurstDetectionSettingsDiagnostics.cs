using Viegard.Application.Burst;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingBurstDetectionSettingsDiagnostics(
    ILogger<LoggingBurstDetectionSettingsDiagnostics> logger) : IBurstDetectionSettingsDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(
            exception,
            "Burst-detection settings refresh failed; retaining the last known good setting set. If no settings row has loaded yet, the detector uses the environment-configured burst-detection settings.");
    }
}
