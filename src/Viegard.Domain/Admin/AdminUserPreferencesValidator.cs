namespace Viegard.Domain.Admin;

public static class AdminUserPreferencesValidator
{
    public static bool TryNormalize(
        Guid userId,
        string? timeZoneId,
        int pageSize,
        int statusRefreshSeconds,
        DateTimeOffset updatedAt,
        out AdminUserPreferences preferences,
        out string error)
    {
        preferences = new AdminUserPreferences
        {
            UserId = userId,
            UpdatedAt = updatedAt.ToUniversalTime(),
        };

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            error = "Select a time zone.";
            return false;
        }

        var normalizedTimeZoneId = timeZoneId.Trim();
        if (!TryResolveTimeZone(normalizedTimeZoneId, out _))
        {
            error = $"Time zone '{normalizedTimeZoneId}' was not recognized.";
            return false;
        }

        preferences = preferences with
        {
            TimeZoneId = normalizedTimeZoneId,
            PageSize = Math.Clamp(pageSize, AdminUserPreferences.MinPageSize, AdminUserPreferences.MaxPageSize),
            StatusRefreshSeconds = Math.Clamp(
                statusRefreshSeconds,
                AdminUserPreferences.MinStatusRefreshSeconds,
                AdminUserPreferences.MaxStatusRefreshSeconds),
        };
        error = string.Empty;
        return true;
    }

    public static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
