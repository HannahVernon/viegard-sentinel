using Viegard.Domain.Health;

namespace Viegard.Application.Configuration;

public static class HostUpgradeTargetList
{
    public static IReadOnlyList<string> BuildKnownTargets(
        IEnumerable<string> commandHistoryTargets,
        IEnumerable<InstanceRegistration> registrations,
        int limit = HostUpgradeCommandPolicy.DefaultRecentLimit)
    {
        ArgumentNullException.ThrowIfNull(commandHistoryTargets);
        ArgumentNullException.ThrowIfNull(registrations);

        var safeLimit = Math.Clamp(limit, 1, HostUpgradeCommandPolicy.MaxRecentLimit);
        var known = new SortedSet<string>(StringComparer.Ordinal)
        {
            HostUpgradeCommandPolicy.DefaultTarget,
        };

        foreach (var target in registrations.Select(registration => registration.UpgradeTarget))
        {
            AddIfValid(target);
        }

        foreach (var target in commandHistoryTargets)
        {
            AddIfValid(target);
        }

        return known
            .OrderBy(target => target == HostUpgradeCommandPolicy.DefaultTarget ? 0 : 1)
            .ThenBy(target => target, StringComparer.Ordinal)
            .Take(safeLimit)
            .ToList();

        void AddIfValid(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            try
            {
                known.Add(HostUpgradeCommandPolicy.NormalizeTarget(target));
            }
            catch (ArgumentException)
            {
                // Registry values are normally validated before write.  If a
                // stale row predates validation, leave the freeform path
                // available rather than rendering an unusable option.
            }
        }
    }
}
