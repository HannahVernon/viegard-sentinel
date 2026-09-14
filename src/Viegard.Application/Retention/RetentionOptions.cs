using Microsoft.Extensions.Options;

namespace Viegard.Application.Retention;

/// <summary>Configures scheduled data-retention purges.  Null retention periods keep rows forever.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Viegard:Retention";
    public const int DefaultBatchSize = 5_000;
    public const int MinBatchSize = 1;
    public const int MaxBatchSize = 50_000;

    public int? RawObservationsDays { get; set; }

    public int? EventsDays { get; set; }

    public int? IncidentsDays { get; set; }

    public int? ClassificationsDays { get; set; }

    public int? DecisionsDays { get; set; }

    public int? ActionsDays { get; set; }

    public int? AuditRecordsDays { get; set; }

    public int? DeadLetteredQueueMessagesDays { get; set; }

    public int? ExpiredAdminSessionsDays { get; set; }

    public int BatchSize { get; set; } = DefaultBatchSize;

    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);

    public int EffectiveBatchSize => Math.Clamp(BatchSize, MinBatchSize, MaxBatchSize);

    public IReadOnlyList<RetentionPeriod> ConfiguredPeriods()
    {
        var periods = new List<RetentionPeriod>(capacity: 9);
        AddConfigured(periods, RetentionTarget.RawObservations, RawObservationsDays);
        AddConfigured(periods, RetentionTarget.Events, EventsDays);
        AddConfigured(periods, RetentionTarget.Incidents, IncidentsDays);
        AddConfigured(periods, RetentionTarget.Classifications, ClassificationsDays);
        AddConfigured(periods, RetentionTarget.Decisions, DecisionsDays);
        AddConfigured(periods, RetentionTarget.Actions, ActionsDays);
        AddConfigured(periods, RetentionTarget.AuditRecords, AuditRecordsDays);
        AddConfigured(periods, RetentionTarget.DeadLetteredQueueMessages, DeadLetteredQueueMessagesDays);
        AddConfigured(periods, RetentionTarget.ExpiredAdminSessions, ExpiredAdminSessionsDays);
        return periods;
    }

    private static void AddConfigured(List<RetentionPeriod> periods, RetentionTarget target, int? days)
    {
        if (days is int configuredDays)
        {
            periods.Add(new RetentionPeriod(target, configuredDays));
        }
    }
}

public sealed record RetentionPeriod(RetentionTarget Target, int Days);

public enum RetentionTarget
{
    RawObservations,
    Events,
    Incidents,
    Classifications,
    Decisions,
    Actions,
    AuditRecords,
    DeadLetteredQueueMessages,
    ExpiredAdminSessions,
}

public static class RetentionTargetNames
{
    public static string TableName(this RetentionTarget target) => target switch
    {
        RetentionTarget.RawObservations => "raw_observations",
        RetentionTarget.Events => "events",
        RetentionTarget.Incidents => "incidents",
        RetentionTarget.Classifications => "classifications",
        RetentionTarget.Decisions => "decisions",
        RetentionTarget.Actions => "actions",
        RetentionTarget.AuditRecords => "audit_records",
        RetentionTarget.DeadLetteredQueueMessages => "queue_messages",
        RetentionTarget.ExpiredAdminSessions => "admin_sessions",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };
}

/// <summary>Startup validation for retention configuration.</summary>
public sealed class RetentionOptionsValidator : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateNonNegative(options.RawObservationsDays, nameof(options.RawObservationsDays), failures);
        ValidateNonNegative(options.EventsDays, nameof(options.EventsDays), failures);
        ValidateNonNegative(options.IncidentsDays, nameof(options.IncidentsDays), failures);
        ValidateNonNegative(options.ClassificationsDays, nameof(options.ClassificationsDays), failures);
        ValidateNonNegative(options.DecisionsDays, nameof(options.DecisionsDays), failures);
        ValidateNonNegative(options.ActionsDays, nameof(options.ActionsDays), failures);
        ValidateNonNegative(options.AuditRecordsDays, nameof(options.AuditRecordsDays), failures);
        ValidateNonNegative(options.DeadLetteredQueueMessagesDays, nameof(options.DeadLetteredQueueMessagesDays), failures);
        ValidateNonNegative(options.ExpiredAdminSessionsDays, nameof(options.ExpiredAdminSessionsDays), failures);

        if (options.BatchSize < 0)
        {
            failures.Add("Retention BatchSize must not be negative.");
        }

        if (options.StartupDelay < TimeSpan.Zero)
        {
            failures.Add("Retention StartupDelay must not be negative.");
        }

        if (options.CheckInterval <= TimeSpan.Zero)
        {
            failures.Add("Retention CheckInterval must be positive.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateNonNegative(int? value, string name, List<string> failures)
    {
        if (value < 0)
        {
            failures.Add($"Retention {name} must not be negative.");
        }
    }
}
