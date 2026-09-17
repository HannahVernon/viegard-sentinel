using Viegard.Application.Policy;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingPolicyPostureDiagnostics(
    ILogger<LoggingPolicyPostureDiagnostics> logger) : IPolicyPostureDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(
            exception,
            "Policy posture refresh failed; retaining the last known good posture. If no settings row has loaded yet, posture consumers use the environment-configured posture.");
    }
}
