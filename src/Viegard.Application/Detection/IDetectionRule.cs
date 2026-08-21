using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Detection;

/// <summary>Pure deterministic rule for scored evidence over one normalized event.</summary>
public interface IDetectionRule
{
    string RuleId { get; }

    IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e);
}
