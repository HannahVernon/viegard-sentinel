using System.Text.Json;

namespace Viegard.Application.Retention;

public sealed record RetentionSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const int FixedId = 1;
    public const int MaxUpdatedByLength = 128;
    public const string SystemSeedActor = "system:retention-seed";

    public static readonly IReadOnlyList<RetentionTarget> Targets =
    [
        RetentionTarget.RawObservations,
        RetentionTarget.Events,
        RetentionTarget.Incidents,
        RetentionTarget.Classifications,
        RetentionTarget.Decisions,
        RetentionTarget.Actions,
        RetentionTarget.AuditRecords,
        RetentionTarget.DeadLetteredQueueMessages,
        RetentionTarget.ExpiredAdminSessions,
    ];

    public int Id { get; init; } = FixedId;

    public int? RawObservationsDays { get; init; }

    public int? EventsDays { get; init; }

    public int? IncidentsDays { get; init; }

    public int? ClassificationsDays { get; init; }

    public int? DecisionsDays { get; init; }

    public int? ActionsDays { get; init; }

    public int? AuditRecordsDays { get; init; }

    public int? DeadLetteredQueueMessagesDays { get; init; }

    public int? ExpiredAdminSessionsDays { get; init; }

    public int Version { get; init; }

    public DateTimeOffset? SeededAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public DateTimeOffset? LastCycleAt { get; init; }

    public string? LastCycleCountsJson { get; init; }

    public IReadOnlyList<RetentionPeriod> ConfiguredPeriods()
    {
        var periods = new List<RetentionPeriod>(Targets.Count);
        foreach (var target in Targets)
        {
            if (GetDays(target) is { } days)
            {
                periods.Add(new RetentionPeriod(target, days));
            }
        }

        return periods;
    }

    public int? GetDays(RetentionTarget target) => target switch
    {
        RetentionTarget.RawObservations => RawObservationsDays,
        RetentionTarget.Events => EventsDays,
        RetentionTarget.Incidents => IncidentsDays,
        RetentionTarget.Classifications => ClassificationsDays,
        RetentionTarget.Decisions => DecisionsDays,
        RetentionTarget.Actions => ActionsDays,
        RetentionTarget.AuditRecords => AuditRecordsDays,
        RetentionTarget.DeadLetteredQueueMessages => DeadLetteredQueueMessagesDays,
        RetentionTarget.ExpiredAdminSessions => ExpiredAdminSessionsDays,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    public RetentionSettings WithDays(RetentionTarget target, int? days) => target switch
    {
        RetentionTarget.RawObservations => this with { RawObservationsDays = days },
        RetentionTarget.Events => this with { EventsDays = days },
        RetentionTarget.Incidents => this with { IncidentsDays = days },
        RetentionTarget.Classifications => this with { ClassificationsDays = days },
        RetentionTarget.Decisions => this with { DecisionsDays = days },
        RetentionTarget.Actions => this with { ActionsDays = days },
        RetentionTarget.AuditRecords => this with { AuditRecordsDays = days },
        RetentionTarget.DeadLetteredQueueMessages => this with { DeadLetteredQueueMessagesDays = days },
        RetentionTarget.ExpiredAdminSessions => this with { ExpiredAdminSessionsDays = days },
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    public IReadOnlyDictionary<RetentionTarget, long> LastCycleCounts()
    {
        if (string.IsNullOrWhiteSpace(LastCycleCountsJson))
        {
            return EmptyCounts();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(LastCycleCountsJson, JsonOptions) ?? [];
            return Targets.ToDictionary(
                target => target,
                target => parsed.GetValueOrDefault(target.SettingName()),
                EqualityComparer<RetentionTarget>.Default);
        }
        catch (JsonException)
        {
            return EmptyCounts();
        }
    }

    public static string SerializeCounts(IReadOnlyDictionary<RetentionTarget, long> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var normalized = Targets.ToDictionary(
            target => target.SettingName(),
            target => counts.GetValueOrDefault(target),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static IReadOnlyDictionary<RetentionTarget, long> EmptyCounts() =>
        Targets.ToDictionary(target => target, _ => 0L, EqualityComparer<RetentionTarget>.Default);

    public static RetentionSettings FromOptions(RetentionOptions options, DateTimeOffset seededAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var utc = seededAt.ToUniversalTime();
        return new RetentionSettings
        {
            Id = FixedId,
            RawObservationsDays = options.RawObservationsDays,
            EventsDays = options.EventsDays,
            IncidentsDays = options.IncidentsDays,
            ClassificationsDays = options.ClassificationsDays,
            DecisionsDays = options.DecisionsDays,
            ActionsDays = options.ActionsDays,
            AuditRecordsDays = options.AuditRecordsDays,
            DeadLetteredQueueMessagesDays = options.DeadLetteredQueueMessagesDays,
            ExpiredAdminSessionsDays = options.ExpiredAdminSessionsDays,
            Version = 1,
            SeededAt = utc,
            UpdatedAt = utc,
            UpdatedBy = SystemSeedActor,
        };
    }
}

public sealed record RetentionTargetDescriptor(
    RetentionTarget Target,
    string SettingName,
    string Label,
    string HelpText);

public static class RetentionTargetMetadata
{
    public static readonly IReadOnlyList<RetentionTargetDescriptor> Descriptors =
    [
        new(
            RetentionTarget.RawObservations,
            "raw_observations",
            "Raw observations",
            "Raw source payloads are deleted after this many days."),
        new(
            RetentionTarget.Events,
            "events",
            "Events",
            "Normalized events are deleted after this many days."),
        new(
            RetentionTarget.Incidents,
            "incidents",
            "Incidents",
            "Closed incidents are deleted after this many days.  Open incidents are never deleted regardless of age."),
        new(
            RetentionTarget.Classifications,
            "classifications",
            "Classifications",
            "Classification rows are deleted after this many days.  Corrections are never purgeable and have no setting."),
        new(
            RetentionTarget.Decisions,
            "decisions",
            "Decisions",
            "Policy decisions are deleted after this many days."),
        new(
            RetentionTarget.Actions,
            "actions",
            "Actions",
            "Action records are deleted after this many days."),
        new(
            RetentionTarget.AuditRecords,
            "audit_records",
            "Audit records",
            "Audit ledger rows are deleted after this many days."),
        new(
            RetentionTarget.DeadLetteredQueueMessages,
            "dead_lettered_queue_messages",
            "Dead-lettered queue messages",
            "Only dead-lettered queue messages are deleted after this many days."),
        new(
            RetentionTarget.ExpiredAdminSessions,
            "expired_admin_sessions",
            "Expired admin sessions",
            "Only revoked or expired admin sessions are deleted after this many days.  Live sessions are never deleted."),
    ];

    public static RetentionTargetDescriptor Descriptor(this RetentionTarget target) =>
        Descriptors.First(d => d.Target == target);

    public static string SettingName(this RetentionTarget target) => target.Descriptor().SettingName;
}

public static class RetentionSettingsValidator
{
    public const string PeriodError = "Retention periods must be blank or non-negative whole days.";

    public static bool TryValidate(RetentionSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var target in RetentionSettings.Targets)
        {
            if (settings.GetDays(target) < 0)
            {
                error = PeriodError;
                return false;
            }
        }

        if (settings.Id != RetentionSettings.FixedId)
        {
            error = "Retention settings row has an invalid id.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string NormalizeUpdatedBy(string updatedBy)
    {
        var normalized = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        return normalized.Length <= RetentionSettings.MaxUpdatedByLength
            ? normalized
            : normalized[..RetentionSettings.MaxUpdatedByLength];
    }
}
