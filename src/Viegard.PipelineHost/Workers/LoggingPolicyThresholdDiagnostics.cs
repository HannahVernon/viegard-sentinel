using Viegard.Application.Policy;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingPolicyThresholdDiagnostics(
    ILogger<LoggingPolicyThresholdDiagnostics> logger) : IPolicyThresholdDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(
            exception,
            "Policy threshold refresh failed; retaining the last known good threshold set. If no settings row has loaded yet, policy evaluation uses the environment-configured thresholds.");
    }
}
