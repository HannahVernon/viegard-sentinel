using System.Net;
using Viegard.Domain.Classifications;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Policy;

public enum IncidentTargetIpKind
{
    Found,
    NotApplicable,
    Unavailable,
}

public sealed record IncidentTargetIpResult(IncidentTargetIpKind Kind, string? Ip, Incident? Incident, string Detail)
{
    public static IncidentTargetIpResult Found(string ip, Incident incident) =>
        new(IncidentTargetIpKind.Found, ip, incident, $"Target IP {ip} resolved from incident {incident.Id}.");

    public static IncidentTargetIpResult NotApplicable(string detail) =>
        new(IncidentTargetIpKind.NotApplicable, null, null, detail);

    public static IncidentTargetIpResult Unavailable(string detail) =>
        new(IncidentTargetIpKind.Unavailable, null, null, detail);
}

public static class IncidentTargetIp
{
    private const string Prefix = "ip=";

    public static IncidentTargetIpResult TryDerive(Classification classification, Incident? incident)
    {
        ArgumentNullException.ThrowIfNull(classification);

        if (classification.SubjectKind == ClassificationSubjectKind.MailMessage)
        {
            return IncidentTargetIpResult.NotApplicable("Mail-message subjects have no policy target IP; protected-address guardrail skipped.");
        }

        if (classification.SubjectKind != ClassificationSubjectKind.Incident)
        {
            return IncidentTargetIpResult.Unavailable($"Classification subject kind {classification.SubjectKind} cannot provide a policy target IP.");
        }

        if (incident is null)
        {
            return IncidentTargetIpResult.Unavailable($"Incident {classification.SubjectId} was not found.");
        }

        if (!incident.CorrelationKey.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return IncidentTargetIpResult.Unavailable(
                $"Incident {incident.Id} correlation key does not start with the expected '{Prefix}' prefix.");
        }

        var end = incident.CorrelationKey.IndexOf('|', Prefix.Length);
        var value = end < 0
            ? incident.CorrelationKey[Prefix.Length..].Trim()
            : incident.CorrelationKey[Prefix.Length..end].Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            return IncidentTargetIpResult.Unavailable($"Incident {incident.Id} correlation key has an empty IP value.");
        }

        if (!IPAddress.TryParse(value, out var parsed))
        {
            return IncidentTargetIpResult.Unavailable($"Incident {incident.Id} correlation key IP value '{value}' is not a valid IP address.");
        }

        var ip = (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString();
        return IncidentTargetIpResult.Found(ip, incident);
    }
}
