namespace Viegard.AdminApi.Errors;

/// <summary>
/// Recognizes database command timeouts so list pages can render a friendly
/// "search took too long" message instead of the generic error page.  Npgsql
/// wraps the timeout (InvalidOperationException -> NpgsqlException ->
/// TimeoutException), so the whole inner chain is walked.
/// </summary>
public static class AdminQueryTimeout
{
    public static bool IsTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
