using Viegard.Domain.Events;

namespace Viegard.Application.Burst;

/// <summary>
/// A firing of the burst detector: a (signal, source) crossed its threshold
/// within the window and was not suppressed by cooldown.  Propose-only: the
/// worker turns this into an incident that flows to a review decision.  It is
/// never itself an action.
/// </summary>
public sealed record BurstFiring(
    string SignalId,
    string SourceKey,
    int Count,
    int Threshold,
    TimeSpan Window,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<Guid> EventIds,
    bool ActionEligible);

/// <summary>
/// Standalone rate-based burst detector.  For each configured signal that matches
/// an event, it records the occurrence in the durable window store, counts within
/// the sliding window, and returns a firing when the count reaches the threshold
/// and no cooldown is active.  Counting is independent of single-event detection
/// rules and of the correlator's incident grouping.
/// </summary>
public sealed class BurstDetector(
    IEnumerable<IBurstSignal> signals,
    IBurstWindowStore windowStore,
    BurstDetectionSettingsSource settingsSource,
    BurstDetectionOptions fallbackOptions)
{
    private const int MaxContributingEventIds = 50;

    private readonly IReadOnlyList<IBurstSignal> _signals = signals.ToList();

    public async ValueTask<IReadOnlyList<BurstFiring>> ObserveAsync(
        NormalizedEvent normalizedEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);

        var values = settingsSource.CurrentValues(fallbackOptions);
        if (!values.GlobalEnabled)
        {
            return [];
        }

        List<BurstFiring>? firings = null;

        foreach (var signal in _signals)
        {
            var config = values.ForSignal(signal.SignalId);
            if (config is null || !config.Enabled)
            {
                continue;
            }

            if (!signal.Matches(normalizedEvent))
            {
                continue;
            }

            var sourceKey = signal.SourceKey(normalizedEvent);
            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                continue;
            }

            var state = await windowStore.RecordAndCountAsync(
                signal.SignalId,
                sourceKey,
                normalizedEvent.Id,
                normalizedEvent.OccurredAt,
                config.Window,
                MaxContributingEventIds,
                cancellationToken).ConfigureAwait(false);

            if (state.Count < config.Threshold)
            {
                continue;
            }

            var allowed = await windowStore.TryBeginCooldownAsync(
                signal.SignalId,
                sourceKey,
                normalizedEvent.OccurredAt,
                config.Cooldown,
                cancellationToken).ConfigureAwait(false);

            if (!allowed)
            {
                continue;
            }

            firings ??= [];
            firings.Add(new BurstFiring(
                signal.SignalId,
                sourceKey,
                state.Count,
                config.Threshold,
                config.Window,
                state.WindowStart,
                state.WindowEnd,
                state.EventIds,
                config.ActionEligible));
        }

        return (IReadOnlyList<BurstFiring>?)firings ?? [];
    }
}
