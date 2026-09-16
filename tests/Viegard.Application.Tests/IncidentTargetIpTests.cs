using Viegard.Application.Policy;
using Viegard.Domain;
using Viegard.Domain.Classifications;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Tests;

public sealed class IncidentTargetIpTests
{
    [Fact]
    public void TryDerive_extracts_canonical_ip_from_prefixed_incident_key()
    {
        var incident = Incident("ip=::ffff:198.51.100.10|window=60s");

        var result = IncidentTargetIp.TryDerive(Classification(incident.Id), incident);

        Assert.Equal(IncidentTargetIpKind.Found, result.Kind);
        Assert.Equal("198.51.100.10", result.Ip);
        Assert.Equal(incident.Id, result.Incident!.Id);
    }

    [Fact]
    public void TryDerive_reports_missing_incident()
    {
        var classification = Classification(ViegardId.New());

        var result = IncidentTargetIp.TryDerive(classification, null);

        Assert.Equal(IncidentTargetIpKind.Unavailable, result.Kind);
        Assert.Contains("was not found", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remote=198.51.100.10")]
    [InlineData("ip=not-an-ip")]
    [InlineData("ip=   |window=60s")]
    public void TryDerive_reports_malformed_correlation_keys(string correlationKey)
    {
        var incident = Incident(correlationKey);

        var result = IncidentTargetIp.TryDerive(Classification(incident.Id), incident);

        Assert.Equal(IncidentTargetIpKind.Unavailable, result.Kind);
        Assert.Null(result.Ip);
    }

    [Fact]
    public void TryDerive_treats_mail_message_subjects_as_not_applicable()
    {
        var classification = Classification(ViegardId.New()) with
        {
            SubjectKind = ClassificationSubjectKind.MailMessage,
        };

        var result = IncidentTargetIp.TryDerive(classification, null);

        Assert.Equal(IncidentTargetIpKind.NotApplicable, result.Kind);
    }

    private static Classification Classification(Guid subjectId) => new()
    {
        Id = ViegardId.New(),
        SubjectKind = ClassificationSubjectKind.Incident,
        SubjectId = subjectId,
        ClassifierId = "test",
        Category = "scanner",
        Confidence = 0.9,
        Severity = 7,
        Reasons = ["test"],
        RecommendedAction = "temp-ban-ip",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Incident Incident(string correlationKey) => new()
    {
        Id = ViegardId.New(),
        CorrelationKey = correlationKey,
        WindowStart = DateTimeOffset.UtcNow.AddMinutes(-5),
        WindowEnd = DateTimeOffset.UtcNow,
        EventIds = [],
        Evidence = [],
        State = IncidentState.Classified,
    };
}
