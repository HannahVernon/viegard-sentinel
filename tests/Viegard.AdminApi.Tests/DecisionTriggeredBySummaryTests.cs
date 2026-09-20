using Viegard.AdminApi.Decisions;
using Viegard.Domain.Classifications;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.AdminApi.Tests;

public sealed class DecisionTriggeredBySummaryTests
{
    [Fact]
    public void Summarize_prefers_grouped_event_kinds_for_incidents()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var classification = IncidentClassification();
        var incident = new Incident
        {
            Id = classification.SubjectId,
            CorrelationKey = "ip=198.51.100.10",
            WindowStart = DateTimeOffset.UtcNow.AddMinutes(-5),
            WindowEnd = DateTimeOffset.UtcNow,
            EventIds = [first, second, third],
            Evidence = [],
            State = IncidentState.Classified,
        };
        var eventsById = new Dictionary<Guid, NormalizedEvent>
        {
            [first] = Event(first, MDaemon(MDaemonEventKind.AuthenticationFailed)),
            [second] = Event(second, MDaemon(MDaemonEventKind.AuthenticationFailed)),
            [third] = Event(third, new SyslogEvent
            {
                PeerIp = "198.51.100.10",
                Message = "drop",
            }),
        };

        var summary = DecisionTriggeredBySummary.Summarize(classification, incident, eventsById);

        Assert.Equal("mdaemon/AuthenticationFailed x2, syslog/message", summary);
    }

    [Fact]
    public void Summarize_limits_kind_list_to_top_two_groups()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var classification = IncidentClassification();
        var incident = new Incident
        {
            Id = classification.SubjectId,
            CorrelationKey = "ip=198.51.100.10",
            WindowStart = DateTimeOffset.UtcNow.AddMinutes(-5),
            WindowEnd = DateTimeOffset.UtcNow,
            EventIds = [first, second, third],
            Evidence = [],
            State = IncidentState.Classified,
        };
        var eventsById = new Dictionary<Guid, NormalizedEvent>
        {
            [first] = Event(first, MDaemon(MDaemonEventKind.AuthenticationFailed)),
            [second] = Event(second, new HttpRequestEvent
            {
                RemoteAddress = "198.51.100.10",
            }),
            [third] = Event(third, new SyslogEvent
            {
                PeerIp = "198.51.100.10",
                Message = "drop",
            }),
        };

        var summary = DecisionTriggeredBySummary.Summarize(classification, incident, eventsById);

        Assert.Equal("http/request, mdaemon/AuthenticationFailed +1 more", summary);
    }

    [Fact]
    public void Summarize_falls_back_to_evidence_when_event_kinds_are_unavailable()
    {
        var classification = IncidentClassification();
        var incident = new Incident
        {
            Id = classification.SubjectId,
            CorrelationKey = "ip=198.51.100.10",
            WindowStart = DateTimeOffset.UtcNow.AddMinutes(-5),
            WindowEnd = DateTimeOffset.UtcNow,
            EventIds = [],
            Evidence =
            [
                new EvidenceItem
                {
                    Description = "Rule D-0099: repeated login failures from one source",
                    Score = 4,
                },
                new EvidenceItem
                {
                    Description = "Evidence truncated: incident evidence item cap reached.",
                    Score = 0,
                },
            ],
            State = IncidentState.Classified,
        };

        var summary = DecisionTriggeredBySummary.Summarize(classification, incident, new Dictionary<Guid, NormalizedEvent>());

        Assert.Equal("repeated login failures from one source", summary);
    }

    [Fact]
    public void Summarize_handles_mail_message_classifications_without_incidents()
    {
        var classification = new Classification
        {
            Id = Guid.NewGuid(),
            SubjectKind = ClassificationSubjectKind.MailMessage,
            SubjectId = Guid.NewGuid(),
            ClassifierId = "mail",
            Category = "spam",
            Confidence = 0.7,
            Severity = 3,
            Reasons = ["mail"],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var summary = DecisionTriggeredBySummary.Summarize(classification, incident: null, new Dictionary<Guid, NormalizedEvent>());

        Assert.Equal("mail/message", summary);
    }

    private static Classification IncidentClassification() => new()
    {
        Id = Guid.NewGuid(),
        SubjectKind = ClassificationSubjectKind.Incident,
        SubjectId = Guid.NewGuid(),
        ClassifierId = "test",
        Category = "scanner",
        Confidence = 0.8,
        Severity = 7,
        Reasons = ["reason"],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static NormalizedEvent Event(Guid id, EventPayload payload) => new()
    {
        Id = id,
        SourceId = "source-1",
        SourceType = "test",
        OccurredAt = DateTimeOffset.UtcNow,
        Entities = [],
        Payload = payload,
        RawObservationId = Guid.NewGuid(),
    };

    private static MDaemonLogEvent MDaemon(MDaemonEventKind kind) => new()
    {
        LogKind = MDaemonLogKind.Imap,
        EventKind = kind,
        Message = kind.ToString(),
    };
}
