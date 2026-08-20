using Microsoft.Extensions.Options;
using Viegard.Application.Policy;

namespace Viegard.PipelineHost.Workers;

public sealed class PolicyWorker(
    IOptions<PolicyOptions> options,
    ILogger<PolicyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policyOptions = options.Value;
        logger.LogInformation(
            "Policy worker active in dry-run posture with policy {PolicyId} {PolicyVersion}.  Phase 6 classification will feed this worker; no classifications queue exists in this increment.  Action threshold confidence {ActionConfidence}, review confidence {ReviewConfidence}, minimum severity {MinSeverity}.",
            DefaultPolicyEngine.DefaultPolicyId,
            DefaultPolicyEngine.DefaultPolicyVersion,
            policyOptions.AiActionConfidence,
            policyOptions.AiReviewConfidence,
            policyOptions.AiActionMinSeverity);

        // Phase 6 will add classification intake.  This placeholder intentionally consumes nothing.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        logger.LogInformation("Policy worker stopping.");
    }
}
