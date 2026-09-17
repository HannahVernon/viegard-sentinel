using Viegard.AdminApi.Components;
using Viegard.Application.Configuration;

namespace Viegard.AdminApi;

/// <summary>
/// Builds the display-ready JSON payload for the /status/upgrades polling
/// endpoint.  All strings are formatted server-side (user time zone via the
/// supplied formatter) so the client script only swaps text content.  The
/// Configuration page uses the same helpers at render time so the initial
/// HTML and later patches always agree.
/// </summary>
public static class HostUpgradeStatusPayload
{
    public sealed record CommandRowPayload(
        string Key,
        string Status,
        string StatusCss,
        string Started,
        string Finished,
        string FullDetail,
        bool HasDetail);

    public sealed record Payload(
        IReadOnlyList<CommandRowPayload> Commands,
        bool StalePending);

    public static Payload Build(
        IReadOnlyList<HostUpgradeCommand> commands,
        DateTimeOffset now,
        Func<DateTimeOffset, string> format)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(format);

        var rows = commands
            .Select(command => new CommandRowPayload(
                Key: command.Id.ToString("N"),
                Status: command.Status.ToString(),
                StatusCss: StatusCss(command.Status),
                Started: command.StartedAt is { } started ? format(started) : string.Empty,
                Finished: command.FinishedAt is { } finished ? format(finished) : string.Empty,
                FullDetail: AdminText.Limit(command.Detail),
                HasDetail: HasDetail(command.Detail)))
            .ToList();

        var stalePending = commands.Any(command => HostUpgradeCommandPolicy.IsPendingStale(command, now));
        return new Payload(rows, stalePending);
    }

    public static string StatusCss(HostUpgradeCommandStatus status) => status switch
    {
        HostUpgradeCommandStatus.Pending => "badge badge-amber",
        HostUpgradeCommandStatus.Running => "badge badge-blue",
        HostUpgradeCommandStatus.Succeeded => "badge badge-green",
        HostUpgradeCommandStatus.Failed => "badge badge-red",
        HostUpgradeCommandStatus.Superseded => "badge",
        _ => "badge",
    };

    public static bool HasDetail(string? detail) =>
        !string.IsNullOrWhiteSpace(detail);
}
