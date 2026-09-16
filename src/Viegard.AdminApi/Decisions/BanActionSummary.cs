using System.Text.Json;
using Viegard.Actions.MikroTik;
using Viegard.Domain.Actions;

namespace Viegard.AdminApi.Decisions;

public static class BanActionSummary
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Summarize(ActionRecord action)
    {
        if (string.IsNullOrWhiteSpace(action.ResultsJson))
        {
            return "No router results yet.";
        }

        try
        {
            var results = JsonSerializer.Deserialize<List<MikroTikRouterActionResult>>(action.ResultsJson, Json) ?? [];
            if (results.Count == 0)
            {
                return "No router results yet.";
            }

            var applied = results.Count(IsApplied);
            if (applied == results.Count)
            {
                return $"applied {applied}/{results.Count}";
            }

            var dryRun = results.Count(result => string.Equals(result.Status, "dry-run", StringComparison.Ordinal));
            if (dryRun == results.Count)
            {
                return $"dry run {dryRun}/{results.Count}";
            }

            var failed = results
                .Where(result => string.Equals(result.Status, "failed", StringComparison.Ordinal))
                .Select(result => string.IsNullOrWhiteSpace(result.RouterName) ? result.RouterId.ToString("N")[..12] : result.RouterName)
                .ToList();
            if (failed.Count > 0)
            {
                return $"failed {string.Join(", ", failed)}";
            }

            return $"{applied}/{results.Count} applied";
        }
        catch (JsonException)
        {
            return "Router results are not readable.";
        }
    }

    private static bool IsApplied(MikroTikRouterActionResult result) =>
        string.Equals(result.Status, "applied", StringComparison.Ordinal)
        || string.Equals(result.Status, "applied-without-call", StringComparison.Ordinal);
}
