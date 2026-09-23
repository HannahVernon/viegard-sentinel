using Viegard.Application.Coalescing;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingIncidentCoalescingSettingsDiagnostics(
    ILogger<LoggingIncidentCoalescingSettingsDiagnostics> logger) : IIncidentCoalescingSettingsDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(
            exception,
            "Incident-coalescing settings refresh failed; retaining the last known good setting set. If no settings row has loaded yet, the correlator uses the environment-configured coalescing settings.");
    }
}
