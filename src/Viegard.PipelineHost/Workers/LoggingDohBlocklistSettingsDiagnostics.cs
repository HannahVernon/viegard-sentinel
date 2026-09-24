using Viegard.Application.Doh;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingDohBlocklistSettingsDiagnostics(
    ILogger<LoggingDohBlocklistSettingsDiagnostics> logger) : IDohBlocklistSettingsDiagnostics
{
    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(
            exception,
            "DoH blocklist settings refresh failed; retaining the last known good setting set. If no settings row has loaded yet, the feature uses the environment-configured DoH blocklist settings.");
    }
}
