using Viegard.Domain.Events;

namespace Viegard.Application.Configuration;

public sealed record IngestionFilter
{
    public const int MaxSourceTypeLength = 64;
    public const int MaxEventKindLength = 128;
    public const int MaxUpdatedByLength = 128;
    public const string MDaemonSourceType = "mdaemon";
    public const string SystemSeedActor = "system:ingestion-filter-seed";

    public required string SourceType { get; init; }

    public required string EventKind { get; init; }

    public required bool Suppressed { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required string UpdatedBy { get; init; }
}

public readonly record struct IngestionFilterKey(string SourceType, string EventKind);

public sealed record IngestionFilterDescriptor(
    MDaemonEventKind EventKind,
    string EventKindName,
    bool Locked,
    bool DefaultSuppressed,
    string HelpText);

public static class MDaemonIngestionFilterPolicy
{
    public const string SourceType = IngestionFilter.MDaemonSourceType;

    private static readonly MDaemonEventKind[] LockedKinds =
    [
        MDaemonEventKind.AuthenticationFailed,
        MDaemonEventKind.IpBlocked,
        MDaemonEventKind.AccessRefused,
        MDaemonEventKind.ScreeningBlocked,
    ];

    private static readonly MDaemonEventKind[] DefaultSuppressedKinds =
    [
        MDaemonEventKind.SessionLine,
        MDaemonEventKind.Other,
    ];

    public static readonly IReadOnlyList<IngestionFilterDescriptor> Descriptors =
        Enum.GetValues<MDaemonEventKind>()
            .Select(kind => new IngestionFilterDescriptor(
                kind,
                kind.ToString(),
                LockedKinds.Contains(kind),
                DefaultSuppressedKinds.Contains(kind),
                HelpText(kind)))
            .ToList();

    public static readonly IReadOnlySet<string> LockedEventKindNames =
        LockedKinds.Select(kind => kind.ToString()).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<IngestionFilter> DefaultFilters(DateTimeOffset updatedAt) =>
        Descriptors
            .Where(descriptor => descriptor.DefaultSuppressed && !descriptor.Locked)
            .Select(descriptor => new IngestionFilter
            {
                SourceType = SourceType,
                EventKind = descriptor.EventKindName,
                Suppressed = true,
                UpdatedAt = updatedAt.ToUniversalTime(),
                UpdatedBy = IngestionFilter.SystemSeedActor,
            })
            .ToList();

    public static bool TryGetKind(string? eventKind, out MDaemonEventKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(eventKind)
            || !Enum.GetNames<MDaemonEventKind>().Contains(eventKind, StringComparer.Ordinal))
        {
            return false;
        }

        return Enum.TryParse(eventKind, ignoreCase: false, out kind);
    }

    public static bool IsLocked(MDaemonEventKind kind) => LockedKinds.Contains(kind);

    public static bool IsLocked(string eventKind) =>
        TryGetKind(eventKind, out var kind) && IsLocked(kind);

    private static string HelpText(MDaemonEventKind kind) => kind switch
    {
        MDaemonEventKind.AuthenticationFailed => "Authentication failures are security signal and are always emitted.",
        MDaemonEventKind.IpBlocked => "Dynamic Screening IP blocks are security signal and are always emitted.",
        MDaemonEventKind.AccessRefused => "Access-refused records can indicate rejected hostile traffic and are always emitted.",
        MDaemonEventKind.ScreeningBlocked => "Screening blocks are security signal and are always emitted.",
        MDaemonEventKind.SessionLine => "Routine session transcript markers; suppressed by default to reduce protocol chatter.",
        MDaemonEventKind.Other => "Unclassified routine transcript lines; suppressed by default to reduce protocol chatter.",
        MDaemonEventKind.ConnectionAccepted => "Connection records are configurable; keep them when source-address context is useful.",
        _ => "MDaemon event kind.",
    };
}

public static class IngestionFilterValidation
{
    public static IReadOnlyList<IngestionFilter> BuildRows(
        string sourceType,
        IReadOnlyDictionary<string, bool> suppressions,
        IReadOnlySet<string> lockedEventKinds,
        string updatedBy,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(suppressions);
        ArgumentNullException.ThrowIfNull(lockedEventKinds);

        var normalizedSourceType = NormalizeSourceType(sourceType);
        ValidateText(normalizedSourceType, nameof(sourceType), IngestionFilter.MaxSourceTypeLength);
        var normalizedUpdatedBy = NormalizeUpdatedBy(updatedBy);
        var utcUpdatedAt = updatedAt.ToUniversalTime();
        var rows = new List<IngestionFilter>(suppressions.Count);

        foreach (var pair in suppressions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var eventKind = NormalizeEventKind(pair.Key);
            ValidateText(eventKind, nameof(pair.Key), IngestionFilter.MaxEventKindLength);
            if (pair.Value && lockedEventKinds.Contains(eventKind))
            {
                throw new InvalidOperationException($"MDaemon event kind {eventKind} is locked and cannot be suppressed.");
            }

            rows.Add(new IngestionFilter
            {
                SourceType = normalizedSourceType,
                EventKind = eventKind,
                Suppressed = pair.Value,
                UpdatedAt = utcUpdatedAt,
                UpdatedBy = normalizedUpdatedBy,
            });
        }

        return rows;
    }

    public static bool TryCreateKey(IngestionFilter filter, out IngestionFilterKey key, out string reason)
    {
        ArgumentNullException.ThrowIfNull(filter);
        key = default;
        reason = string.Empty;

        var sourceType = NormalizeSourceType(filter.SourceType);
        var eventKind = NormalizeEventKind(filter.EventKind);
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            reason = "source_type is blank";
            return false;
        }

        if (sourceType.Length > IngestionFilter.MaxSourceTypeLength)
        {
            reason = "source_type is too long";
            return false;
        }

        if (string.IsNullOrWhiteSpace(eventKind))
        {
            reason = "event_kind is blank";
            return false;
        }

        if (eventKind.Length > IngestionFilter.MaxEventKindLength)
        {
            reason = "event_kind is too long";
            return false;
        }

        if (sourceType == MDaemonIngestionFilterPolicy.SourceType)
        {
            if (!MDaemonIngestionFilterPolicy.TryGetKind(eventKind, out var kind))
            {
                reason = $"MDaemon event_kind '{eventKind}' is not recognized by this build";
                return false;
            }

            if (filter.Suppressed && MDaemonIngestionFilterPolicy.IsLocked(kind))
            {
                reason = $"MDaemon event_kind '{eventKind}' is locked and cannot be suppressed";
                return false;
            }
        }

        key = new IngestionFilterKey(sourceType, eventKind);
        return true;
    }

    public static string NormalizeSourceType(string sourceType) =>
        (sourceType ?? string.Empty).Trim().ToLowerInvariant();

    public static string NormalizeEventKind(string eventKind) =>
        (eventKind ?? string.Empty).Trim();

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= IngestionFilter.MaxUpdatedByLength
            ? normalized
            : normalized[..IngestionFilter.MaxUpdatedByLength];
    }

    private static void ValidateText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        if (value.Length > maxLength)
        {
            throw new InvalidOperationException($"{name} must be {maxLength} characters or fewer.");
        }
    }
}
