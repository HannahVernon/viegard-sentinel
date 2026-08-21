namespace Viegard.Domain.Events;

/// <summary>An email address participant.  All values are untrusted observed data.</summary>
public sealed record MailAddressInfo
{
    public string? DisplayName { get; init; }

    public required string Address { get; init; }
}

/// <summary>Attachment metadata.  Attachment content is never retrieved during ingestion (D-0022).</summary>
public sealed record AttachmentInfo
{
    /// <summary>Untrusted: may contain malicious or misleading names.</summary>
    public string? FileName { get; init; }

    public string? ContentType { get; init; }

    public long? SizeBytes { get; init; }

    public bool IsInline { get; init; }
}

/// <summary>
/// Normalized event payload for an observed mail message.  Every string in
/// this record is untrusted observed data.
/// </summary>
public sealed record MailMessageEvent : EventPayload
{
    /// <summary>The configured account this message was observed in.</summary>
    public required string AccountId { get; init; }

    public required string Folder { get; init; }

    /// <summary>IMAP UID within the folder's current UIDVALIDITY.</summary>
    public required uint Uid { get; init; }

    public string? MessageId { get; init; }

    public required IReadOnlyList<MailAddressInfo> From { get; init; }

    public IReadOnlyList<MailAddressInfo> ReplyTo { get; init; } = [];

    public IReadOnlyList<MailAddressInfo> To { get; init; } = [];

    public IReadOnlyList<MailAddressInfo> Cc { get; init; } = [];

    public string? Subject { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    public string? TextBody { get; init; }

    public string? HtmlBody { get; init; }

    /// <summary>Links extracted from the text and HTML bodies.  Untrusted.</summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
}
