using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;

namespace Viegard.AdminApi.Errors;

/// <summary>
/// Records unhandled admin-request exceptions for the step-up-gated /errors
/// page.  Recording is fail-soft: a capture failure is logged and never
/// interferes with rendering the error page itself.
/// </summary>
public static class AdminErrorRecorder
{
    public static async ValueTask TryRecordAsync(HttpContext context, IAdminErrorStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var feature = context.Features.Get<IExceptionHandlerPathFeature>();
            if (feature?.Error is not { } exception)
            {
                return;
            }

            var error = new AdminError
            {
                Id = ViegardId.New(),
                OccurredAt = DateTimeOffset.UtcNow,
                RequestId = Bound(Activity.Current?.Id ?? context.TraceIdentifier, AdminError.MaxRequestIdLength),
                Path = Bound(feature.Path ?? context.Request.Path.ToString(), AdminError.MaxPathLength),
                Method = Bound(context.Request.Method, AdminError.MaxMethodLength),
                Username = BoundOrNull(context.User.Identity?.Name, AdminError.MaxUsernameLength),
                ExceptionType = Bound(exception.GetType().FullName ?? exception.GetType().Name, AdminError.MaxExceptionTypeLength),
                Message = Bound(exception.Message, AdminError.MaxMessageLength),
                StackTrace = Bound(exception.ToString(), AdminError.MaxStackTraceLength),
            };

            // CancellationToken.None: the client may already be gone, and the
            // record should survive regardless.
            await store.AddAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record an unhandled admin error for the /errors page.");
        }
    }

    private static string Bound(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? BoundOrNull(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Bound(value, maxLength);
}
