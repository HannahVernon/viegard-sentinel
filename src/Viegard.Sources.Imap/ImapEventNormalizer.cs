using System.Text.Json;
using Viegard.Application.Sources;
using Viegard.Domain.Events;

namespace Viegard.Sources.Imap;

/// <summary>
/// Normalizes IMAP raw payloads (<see cref="MailFetchDto"/> JSON) into
/// <see cref="MailMessageEvent"/>s.  Malformed payloads yield a failure
/// result; they never throw (ingestion must survive hostile input).
/// </summary>
public sealed class ImapEventNormalizer : IEventNormalizer
{
    public const string ImapSourceType = "imap";

    /// <summary>Body text beyond this length is truncated in the normalized event.</summary>
    public const int MaxBodyLength = 262_144;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string SourceType => ImapSourceType;

    public NormalizationResult Normalize(RawObservation observation, string rawPayload)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return NormalizationResult.Failure("Empty IMAP payload.");
        }

        MailFetchDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MailFetchDto>(rawPayload, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return NormalizationResult.Failure($"IMAP payload is not valid MailFetchDto JSON: {ex.Message}");
        }

        if (dto is null)
        {
            return NormalizationResult.Failure("IMAP payload deserialized to null.");
        }

        if (dto.SchemaVersion != 1)
        {
            return NormalizationResult.Failure($"Unsupported MailFetchDto schema version {dto.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(dto.AccountId) || string.IsNullOrWhiteSpace(dto.Folder))
        {
            return NormalizationResult.Failure("MailFetchDto is missing AccountId or Folder.");
        }

        var textBody = Truncate(dto.TextBody);
        var htmlBody = Truncate(dto.HtmlBody);

        var payload = new MailMessageEvent
        {
            AccountId = dto.AccountId,
            Folder = dto.Folder,
            Uid = dto.Uid,
            MessageId = dto.MessageId,
            From = Map(dto.From),
            ReplyTo = Map(dto.ReplyTo),
            To = Map(dto.To),
            Cc = Map(dto.Cc),
            Subject = dto.Subject,
            SentAt = dto.SentAt,
            TextBody = textBody,
            HtmlBody = htmlBody,
            Links = LinkExtractor.Extract(textBody, htmlBody),
            Attachments = dto.Attachments
                .Select(a => new AttachmentInfo
                {
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    IsInline = a.IsInline,
                })
                .ToList(),
        };

        var entities = new List<EntityRef>();
        foreach (var from in payload.From)
        {
            entities.Add(new EntityRef(EntityKind.EmailAddress, from.Address));
        }

        return NormalizationResult.Success(new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = observation.SourceId,
            SourceType = ImapSourceType,
            OccurredAt = dto.SentAt ?? observation.ObservedAt,
            Entities = entities,
            Payload = payload,
            RawObservationId = observation.Id,
        });
    }

    private static IReadOnlyList<MailAddressInfo> Map(IReadOnlyList<MailAddressDto> addresses) =>
        addresses
            .Where(a => !string.IsNullOrWhiteSpace(a.Address))
            .Select(a => new MailAddressInfo { DisplayName = a.DisplayName, Address = a.Address })
            .ToList();

    private static string? Truncate(string? body) =>
        body is { Length: > MaxBodyLength }
            ? body[..MaxBodyLength] + "\n[TRUNCATED BY VIEGARD]"
            : body;
}
