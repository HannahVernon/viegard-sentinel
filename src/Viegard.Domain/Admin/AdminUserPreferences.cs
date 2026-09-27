namespace Viegard.Domain.Admin;

/// <summary>
/// Per-account display preferences for the admin interface.  These affect
/// presentation only, never security behavior, so editing them does not
/// require step-up verification.
/// </summary>
public sealed record AdminUserPreferences
{
    public const int MinPageSize = 10;
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    public const int MinStatusRefreshSeconds = 5;
    public const int MaxStatusRefreshSeconds = 300;
    public const int DefaultStatusRefreshSeconds = 30;

    public const int MaxDecisionsViewLength = 512;

    /// <summary>The account these preferences belong to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// Time zone identifier used to render timestamps (IANA or Windows id;
    /// resolved via TimeZoneInfo).  UTC by default.
    /// </summary>
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>Default list page size when no explicit size is requested.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>
    /// Interval in seconds between status polls (header traffic light and
    /// the live /queues values).
    /// </summary>
    public int StatusRefreshSeconds { get; init; } = DefaultStatusRefreshSeconds;

    /// <summary>
    /// Remembered filter and sort state for the /decisions list, stored as a
    /// canonical query string (for example "outcome=RequireApproval&amp;unreviewed=1").
    /// Empty means no remembered view, so the list opens unfiltered.  This is a
    /// sticky-view convenience only; it never affects which decisions exist or
    /// who may act on them.
    /// </summary>
    public string DecisionsView { get; init; } = string.Empty;

    public required DateTimeOffset UpdatedAt { get; init; }
}
