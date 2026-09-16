namespace Viegard.AdminApi.Decisions;

public static class DecisionReviewDurations
{
    public const string OneDay = "1d";
    public const string SevenDays = "7d";
    public const string ThirtyDays = "30d";

    public static IReadOnlyList<DecisionReviewDurationOption> Options { get; } =
    [
        new(OneDay, "1 day", TimeSpan.FromDays(1)),
        new(SevenDays, "7 days", TimeSpan.FromDays(7)),
        new(ThirtyDays, "30 days", TimeSpan.FromDays(30)),
    ];

    public static bool TryGet(string value, out DecisionReviewDurationOption option)
    {
        foreach (var candidate in Options)
        {
            if (string.Equals(candidate.Value, value, StringComparison.Ordinal))
            {
                option = candidate;
                return true;
            }
        }

        option = default!;
        return false;
    }
}

public sealed record DecisionReviewDurationOption(string Value, string Label, TimeSpan Duration);
