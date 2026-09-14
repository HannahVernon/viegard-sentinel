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

    /// <summary>The account these preferences belong to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// Time zone identifier used to render timestamps (IANA or Windows id;
    /// resolved via TimeZoneInfo).  UTC by default.
    /// </summary>
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>Default list page size when no explicit size is requested.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    public required DateTimeOffset UpdatedAt { get; init; }
}
