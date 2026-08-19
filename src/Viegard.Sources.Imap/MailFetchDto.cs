namespace Viegard.Sources.Imap;

/// <summary>
/// Serialization contract between the IMAP fetcher and the normalizer: the
/// "raw payload" of an IMAP observation is this record as JSON.  Keeping the
/// contract explicit lets the normalizer be tested against fixtures without
/// any IMAP server, and preserves what was observed for audit/replay.
/// All fields are untrusted observed data.
/// </summary>
public sealed record MailFetchDto
{
    public required int SchemaVersion { get; init; }

    public required string AccountId { get; init; }

    public required string Folder { get; init; }

    public required uint Uid { get; init; }

    public string? MessageId { get; init; }

    public IReadOnlyList<MailAddressDto> From { get; init; } = [];

    public IReadOnlyList<MailAddressDto> ReplyTo { get; init; } = [];

    public IReadOnlyList<MailAddressDto> To { get; init; } = [];

    public IReadOnlyList<MailAddressDto> Cc { get; init; } = [];

    public string? Subject { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    public string? TextBody { get; init; }

    public string? HtmlBody { get; init; }

    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
}

public sealed record MailAddressDto
{
    public string? DisplayName { get; init; }

    public required string Address { get; init; }
}

public sealed record AttachmentDto
{
    public string? FileName { get; init; }

    public string? ContentType { get; init; }

    public long? SizeBytes { get; init; }

    public bool IsInline { get; init; }
}
