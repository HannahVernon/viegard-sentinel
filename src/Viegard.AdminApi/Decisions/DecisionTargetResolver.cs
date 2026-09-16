using Viegard.Application.Policy;
using Viegard.Application.Stores;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;

namespace Viegard.AdminApi.Decisions;

public sealed class DecisionTargetResolver(
    IClassificationStore classifications,
    IIncidentStore incidents)
{
    public async ValueTask<DecisionTargetResolution> ResolveAsync(
        Decision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var classification = await classifications.GetAsync(decision.ClassificationId, cancellationToken).ConfigureAwait(false);
        if (classification is null)
        {
            return new DecisionTargetResolution(
                null,
                IncidentTargetIpResult.Unavailable($"Classification {decision.ClassificationId} was not found."));
        }

        var incident = classification.SubjectKind == ClassificationSubjectKind.Incident
            ? await incidents.GetAsync(classification.SubjectId, cancellationToken).ConfigureAwait(false)
            : null;
        return new DecisionTargetResolution(classification, IncidentTargetIp.TryDerive(classification, incident));
    }
}

public sealed record DecisionTargetResolution(Classification? Classification, IncidentTargetIpResult Target);
