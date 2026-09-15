using System.Globalization;
using System.Security.Claims;
using Viegard.Application.Logging;
using Viegard.Application.Stores;
using Viegard.Domain.Admin;

namespace Viegard.AdminApi;

public sealed class UserDisplay(
    IHttpContextAccessor httpContextAccessor,
    IAdminUserStore users,
    ILogger<UserDisplay> logger)
{
    private Task? _initializeTask;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private int _defaultPageSize = AdminUserPreferences.DefaultPageSize;
    private int _statusRefreshSeconds = AdminUserPreferences.DefaultStatusRefreshSeconds;
    private bool _invalidTimeZoneLogged;

    public int DefaultPageSize => _defaultPageSize;

    public int StatusRefreshSeconds => _statusRefreshSeconds;

    public Task InitializeAsync() => _initializeTask ??= InitializeCoreAsync();

    public async ValueTask<int> DefaultPageSizeAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        return DefaultPageSize;
    }

    public async ValueTask<string> FormatAsync(DateTimeOffset value)
    {
        await InitializeAsync().ConfigureAwait(false);
        return Format(value);
    }

    public async ValueTask<string> FormatAsync(DateTimeOffset? value) =>
        value is null ? string.Empty : await FormatAsync(value.Value).ConfigureAwait(false);

    public string Format(DateTimeOffset value) => Format(value, _timeZone);

    public string Format(DateTimeOffset? value) => value is null ? string.Empty : Format(value.Value);

    public static string Format(DateTimeOffset value, TimeZoneInfo timeZone)
    {
        var converted = TimeZoneInfo.ConvertTime(value, timeZone);
        var suffix = converted.Offset == TimeSpan.Zero ? "Z" : FormatOffset(converted.Offset);
        return converted.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + suffix;
    }

    private async Task InitializeCoreAsync()
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null
            || context.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return;
        }

        var preferences = await users.GetPreferencesAsync(userId, context.RequestAborted).ConfigureAwait(false);
        _defaultPageSize = Math.Clamp(
            preferences.PageSize,
            AdminUserPreferences.MinPageSize,
            AdminUserPreferences.MaxPageSize);
        _statusRefreshSeconds = Math.Clamp(
            preferences.StatusRefreshSeconds,
            AdminUserPreferences.MinStatusRefreshSeconds,
            AdminUserPreferences.MaxStatusRefreshSeconds);
        _timeZone = ResolveTimeZoneOrUtc(preferences.TimeZoneId);
    }

    private TimeZoneInfo ResolveTimeZoneOrUtc(string timeZoneId)
    {
        if (AdminUserPreferencesValidator.TryResolveTimeZone(timeZoneId, out var timeZone))
        {
            return timeZone;
        }

        if (!_invalidTimeZoneLogged)
        {
            logger.LogWarning(
                "Admin preference time zone {TimeZoneId} could not be resolved.  Falling back to UTC.",
                LogSanitizer.Sanitize(timeZoneId));
            _invalidTimeZoneLogged = true;
        }

        return TimeZoneInfo.Utc;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var absolute = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{(int)absolute.TotalHours:00}:{absolute.Minutes:00}");
    }
}
