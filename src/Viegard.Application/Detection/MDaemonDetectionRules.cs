using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Detection;

public sealed class MDaemonDetectionRule : IDetectionRule
{
    public string RuleId => "mdaemon.security-event";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not MDaemonLogEvent mdaemon)
        {
            return [];
        }

        var (score, description) = mdaemon.EventKind switch
        {
            MDaemonEventKind.AuthenticationFailed => (0.4, "MDaemon reported an authentication failure."),
            MDaemonEventKind.IpBlocked => (0.9, "MDaemon blocked the IP address."),
            MDaemonEventKind.ScreeningBlocked => (0.6, "MDaemon screening blocked the connection."),
            MDaemonEventKind.AccessRefused => (0.2, "MDaemon refused access to a blocked or screened IP address."),
            _ => (0.0, string.Empty),
        };

        return score <= 0
            ? []
            : [DetectionText.Evidence(RuleId, description, score, e.Id)];
    }
}
