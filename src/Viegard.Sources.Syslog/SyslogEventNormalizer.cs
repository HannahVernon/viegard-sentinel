using System.Text.Json;
using Viegard.Application.Sources;
using Viegard.Domain.Events;

namespace Viegard.Sources.Syslog;

/// <summary>
/// Normalizes syslog datagrams: parses the envelope, then routes nginx
/// access-log tags to the nginx parser (producing <see cref="HttpRequestEvent"/>);
/// everything else (including unparseable nginx lines) becomes a generic
/// <see cref="SyslogEvent"/>.  Fails closed on malformed DTO payloads.
/// </summary>
public sealed class SyslogEventNormalizer(SyslogSourceOptions options) : IEventNormalizer
{
    public const string SyslogSourceType = "syslog";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string SourceType => SyslogSourceType;

    public NormalizationResult Normalize(RawObservation observation, string rawPayload)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return NormalizationResult.Failure("Empty syslog payload.");
        }

        SyslogDatagramDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SyslogDatagramDto>(rawPayload, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return NormalizationResult.Failure($"Syslog payload is not valid SyslogDatagramDto JSON: {ex.Message}");
        }

        if (dto is null)
        {
            return NormalizationResult.Failure("Syslog payload deserialized to null.");
        }

        if (dto.SchemaVersion != 1)
        {
            return NormalizationResult.Failure($"Unsupported SyslogDatagramDto schema version {dto.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(dto.PeerIp))
        {
            return NormalizationResult.Failure("SyslogDatagramDto is missing PeerIp.");
        }

        var envelope = SyslogEnvelopeParser.Parse(dto.Raw, dto.ReceivedAt);

        EventPayload payload;
        var entities = new List<EntityRef>();

        var isNginxAccess = envelope.Tag is not null
            && options.NginxAccessTags.Contains(envelope.Tag, StringComparer.OrdinalIgnoreCase);
        var httpEvent = isNginxAccess ? NginxAccessLogParser.Parse(envelope.Message) : null;

        if (httpEvent is not null)
        {
            payload = httpEvent;
            entities.Add(new EntityRef(EntityKind.IpAddress, httpEvent.RemoteAddress));
            if (httpEvent.Host is not null)
            {
                entities.Add(new EntityRef(EntityKind.Host, httpEvent.Host));
            }

            if (httpEvent.Uri is not null)
            {
                entities.Add(new EntityRef(EntityKind.Uri, httpEvent.Uri));
            }

            if (httpEvent.UserAgent is not null)
            {
                entities.Add(new EntityRef(EntityKind.UserAgent, httpEvent.UserAgent));
            }
        }
        else
        {
            payload = new SyslogEvent
            {
                PeerIp = dto.PeerIp,
                Facility = envelope.Facility,
                Severity = envelope.Severity,
                ClaimedHostname = envelope.ClaimedHostname,
                Tag = envelope.Tag,
                Message = envelope.Message,
                ReportedAt = envelope.Timestamp,
            };
            entities.Add(new EntityRef(EntityKind.IpAddress, dto.PeerIp));
        }

        return NormalizationResult.Success(new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = observation.SourceId,
            SourceType = SyslogSourceType,
            OccurredAt = httpEvent?.RequestedAt ?? envelope.Timestamp ?? dto.ReceivedAt,
            Entities = entities,
            Payload = payload,
            RawObservationId = observation.Id,
        });
    }
}
