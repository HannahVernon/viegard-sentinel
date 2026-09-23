using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Viegard.Application.Auth;

namespace Viegard.AdminApi.Auth;

/// <summary>
/// Captures a step-up-gated POST when verification is missing and replays it
/// once the operator completes step-up (D-0053).  Capture stores the request
/// path and a snapshot of the submitted form (including the antiforgery request
/// token) in the process-local <see cref="IPendingStepUpActionStore"/>.  Replay
/// rewrites the current request into the original POST and invokes the target
/// endpoint's delegate directly, so the endpoint re-runs its own step-up gate,
/// antiforgery validation, and business rules exactly as if the operator had
/// resubmitted.
/// </summary>
public static class StepUpResume
{
    /// <summary>Path the step-up completion redirects to when a pending action exists.</summary>
    public const string ContinuePath = "/auth/step-up/continue";

    public static bool TryGetSessionId(HttpContext context, out Guid sessionId) =>
        Guid.TryParse(context.User.FindFirstValue(AdminCookieNames.SessionIdClaim), out sessionId);

    /// <summary>
    /// Snapshots the current POST and its form so it can be resumed after
    /// step-up.  A no-op when the request has no session identity.
    /// </summary>
    public static void Capture(HttpContext context, IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(form);
        if (!TryGetSessionId(context, out var sessionId))
        {
            return;
        }

        var store = context.RequestServices.GetRequiredService<IPendingStepUpActionStore>();
        var source = context.RequestServices.GetRequiredService<SessionSecuritySettingsSource>();
        var options = context.RequestServices.GetRequiredService<IOptions<SessionSecurityOptions>>().Value;
        var ttl = source.CurrentValues(options).ResumeStashTtl;

        var snapshot = form
            .Where(field => !string.IsNullOrEmpty(field.Key))
            .Select(field => new KeyValuePair<string, string[]>(
                field.Key,
                field.Value.Select(value => value ?? string.Empty).ToArray()))
            .ToArray();
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        store.Save(sessionId, new PendingStepUpAction(path, snapshot, DateTimeOffset.UtcNow.Add(ttl)));
    }

    public static bool HasPending(HttpContext context)
    {
        if (!TryGetSessionId(context, out var sessionId))
        {
            return false;
        }

        var store = context.RequestServices.GetRequiredService<IPendingStepUpActionStore>();
        return store.TryPeek(sessionId, DateTimeOffset.UtcNow, out _);
    }

    /// <summary>
    /// Rewrites the current request into the captured POST and invokes the
    /// matching endpoint.  Returns false when no step-up-gated POST endpoint
    /// matches the stored path, leaving the response untouched.
    /// </summary>
    public static async Task<bool> TryDispatchAsync(HttpContext context, PendingStepUpAction action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);

        var dataSource = context.RequestServices.GetRequiredService<EndpointDataSource>();
        RouteEndpoint? target = null;
        foreach (var endpoint in dataSource.Endpoints)
        {
            if (endpoint is RouteEndpoint route
                && PathMatches(route, action.Path)
                && (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) ?? false))
            {
                target = route;
                break;
            }
        }

        if (target?.RequestDelegate is null)
        {
            return false;
        }

        context.Request.Method = HttpMethods.Post;
        context.Request.Path = action.Path;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        foreach (var field in action.Form)
        {
            fields[field.Key] = new StringValues(field.Value);
        }

        context.Request.Form = new FormCollection(fields);
        context.SetEndpoint(target);
        await target.RequestDelegate(context).ConfigureAwait(false);
        return true;
    }

    private static bool PathMatches(RouteEndpoint route, string path) =>
        string.Equals(
            (route.RoutePattern.RawText ?? string.Empty).Trim('/'),
            path.Trim('/'),
            StringComparison.OrdinalIgnoreCase);
}
