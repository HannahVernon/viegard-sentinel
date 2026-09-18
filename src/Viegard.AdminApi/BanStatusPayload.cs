using Viegard.AdminApi.Components;
using Viegard.AdminApi.Decisions;
using Viegard.Domain.Actions;

namespace Viegard.AdminApi;

/// <summary>
/// Builds the display-ready JSON payload for the /status/bans polling
/// endpoint.  All strings are formatted server-side (user time zone via the
/// supplied formatter) so the client script only swaps text content and
/// clones server-rendered row templates.
/// </summary>
public static class BanStatusPayload
{
    public sealed record ActiveBanPayload(
        string Ip,
        string ExpiresIn,
        string Created,
        string DecisionId,
        string DecisionShort);

    public sealed record BanActionPayload(
        string Key,
        string Operation,
        string Status,
        string StatusCss,
        string Requested,
        string Completed,
        string Summary,
        string DecisionId,
        string DecisionShort);

    public sealed record Payload(
        IReadOnlyList<ActiveBanPayload> ActiveBans,
        IReadOnlyList<BanActionPayload> RecentActions);

    public static Payload Build(
        IReadOnlyList<ActiveBan> activeBans,
        IReadOnlyList<ActionRecord> recentActions,
        DateTimeOffset now,
        Func<DateTimeOffset, string> format)
    {
        ArgumentNullException.ThrowIfNull(activeBans);
        ArgumentNullException.ThrowIfNull(recentActions);
        ArgumentNullException.ThrowIfNull(format);

        var bans = activeBans
            .Select(ban => new ActiveBanPayload(
                ban.Ip,
                AdminText.Age(ban.ExpiresAt - now),
                format(ban.CreatedAt),
                ban.DecisionId.ToString("N"),
                AdminText.ShortId(ban.DecisionId)))
            .ToList();

        var actions = recentActions
            .Select(action => new BanActionPayload(
                action.Id.ToString("N"),
                action.OperationId,
                action.Status.ToString(),
                StatusCss(action.Status),
                format(action.RequestedAt),
                action.CompletedAt is null ? "-" : format(action.CompletedAt.Value),
                BanActionSummary.Summarize(action),
                action.DecisionId.ToString("N"),
                AdminText.ShortId(action.DecisionId)))
            .ToList();

        return new Payload(bans, actions);
    }

    public static string StatusCss(ActionStatus status) => status switch
    {
        ActionStatus.Succeeded => "badge badge-green",
        ActionStatus.DryRun => "badge badge-blue",
        ActionStatus.Pending => "badge badge-amber",
        ActionStatus.Failed => "badge badge-red",
        ActionStatus.RolledBack => "badge",
        _ => "badge",
    };
}
