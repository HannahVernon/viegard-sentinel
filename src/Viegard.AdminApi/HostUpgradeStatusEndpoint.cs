using Viegard.Application.Configuration;

namespace Viegard.AdminApi;

internal static class HostUpgradeStatusEndpoint
{
    public static async Task<HostUpgradeStatusPayload.Payload> BuildPayloadAsync(
        IHostUpgradeCommandStore hostUpgrades,
        UserDisplay display,
        CancellationToken cancellationToken)
    {
        await display.InitializeAsync().ConfigureAwait(false);
        var commands = await hostUpgrades
            .ListRecentAsync(target: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return HostUpgradeStatusPayload.Build(commands, DateTimeOffset.UtcNow, display.Format);
    }
}
