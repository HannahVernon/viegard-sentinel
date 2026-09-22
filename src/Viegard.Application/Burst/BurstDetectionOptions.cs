namespace Viegard.Application.Burst;

/// <summary>
/// Code-level fallback defaults for the rate-based burst detector.  These are
/// used only until the durable <see cref="BurstDetectionSettings"/> row is
/// seeded; the Admin UI and read-only API always reflect the seeded values.
/// </summary>
public sealed class BurstDetectionOptions
{
    /// <summary>Master switch.  When false, no burst signals are evaluated.</summary>
    public bool GlobalEnabled { get; set; } = true;

    /// <summary>Whether the repeated admin auth-failure signal is evaluated.</summary>
    public bool AuthFailureEnabled { get; set; } = true;

    /// <summary>Number of matching auth failures from one source within the window that fires a proposal.</summary>
    public int AuthFailureThreshold { get; set; } = 5;

    /// <summary>Sliding-window length, in seconds, over which auth failures are counted.</summary>
    public int AuthFailureWindowSeconds { get; set; } = 300;

    /// <summary>Minimum seconds between successive proposals for the same source, to avoid duplicate reviews.</summary>
    public int AuthFailureCooldownSeconds { get; set; } = 3600;

    /// <summary>
    /// When false (the default), a fired proposal is review-only and never eligible
    /// for an unattended action, keeping the burst detector propose-only.
    /// </summary>
    public bool AuthFailureActionEligible { get; set; }
}
