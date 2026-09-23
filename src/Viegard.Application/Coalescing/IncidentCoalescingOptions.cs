namespace Viegard.Application.Coalescing;

/// <summary>
/// Code-level fallback defaults for incident coalescing.  These are used only
/// until the durable <see cref="IncidentCoalescingSettings"/> row is seeded; the
/// Admin UI and read-only API always reflect the seeded values.
/// </summary>
public sealed class IncidentCoalescingOptions
{
    public const string SectionName = "Viegard:IncidentCoalescing";

    /// <summary>
    /// Master switch.  When false, each event decides in isolation (the prior
    /// behaviour) and no incident coalescing or superseding occurs.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Quiet period, in seconds, after the most recent same-source event before
    /// an incident is finalized.  A provisional decision is emitted immediately;
    /// later events arriving within this window are merged into one final
    /// decision that supersedes the provisional one.
    /// </summary>
    public int SettleWindowSeconds { get; set; } = 10;

    /// <summary>
    /// Absolute cap, in seconds from the incident's first event, on how long an
    /// incident may keep coalescing regardless of continued arrivals.  Bounds
    /// worst-case decision latency for a sustained burst.
    /// </summary>
    public int MaxCoalesceWindowSeconds { get; set; } = 300;
}

/// <summary>Startup validation for incident-coalescing configuration.</summary>
public sealed class IncidentCoalescingOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<IncidentCoalescingOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, IncidentCoalescingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.SettleWindowSeconds < 1)
        {
            failures.Add($"Incident coalescing {nameof(options.SettleWindowSeconds)} must be at least 1.");
        }

        if (options.MaxCoalesceWindowSeconds < options.SettleWindowSeconds)
        {
            failures.Add($"Incident coalescing {nameof(options.MaxCoalesceWindowSeconds)} must be at least {nameof(options.SettleWindowSeconds)}.");
        }

        return failures.Count > 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail(failures)
            : Microsoft.Extensions.Options.ValidateOptionsResult.Success;
    }
}
