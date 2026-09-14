using Viegard.Application.Configuration;
using Viegard.Domain.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class LoggingCustomSignatureRuleDiagnostics(ILogger<LoggingCustomSignatureRuleDiagnostics> logger)
    : ICustomSignatureRuleDiagnostics
{
    public void InvalidSignatureSkipped(CustomSignature signature, IReadOnlyList<string> errors)
    {
        logger.LogWarning(
            "Custom signature {SignatureId} ({SignatureName}) version {Version} was skipped: {Errors}",
            signature.Id,
            signature.Name,
            signature.Version,
            string.Join(" ", errors));
    }

    public void RefreshFailed(Exception exception)
    {
        logger.LogWarning(exception, "Custom signature refresh failed; keeping the last-known-good rule set.");
    }
}
