namespace Viegard.Persistence.Postgres.Model;

/// <summary>
/// EF Core row types.  These are persistence-shaped mirrors of the domain
/// records (which stay immutable and EF-free); Mapping.cs converts between
/// the two.  JSON columns are jsonb.  Low-cardinality descriptors
/// (source, classifier, policy, action provider) are normalized into
/// insert-only reference tables (D-0031); fact rows carry int foreign keys
/// and ReferenceResolver translates to and from the domain's strings.
/// </summary>
public sealed class SourceRow
{
    public int Id { get; set; }

    /// <summary>Natural key: the configured data-source instance identifier.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>
    /// Kind of source ("imap", "syslog", "mdaemon").  Null only when the
    /// key was first seen from a context that does not know the type
    /// (audit); filled once, never rewritten.
    /// </summary>
    public string? SourceType { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
}

public sealed class ClassifierRow
{
    public int Id { get; set; }

    public string ClassifierKey { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
}

public sealed class PolicyRow
{
    public int Id { get; set; }

    public string PolicyKey { get; set; } = string.Empty;

    public string PolicyVersion { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
}

public sealed class ActionProviderRow
{
    public int Id { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
}

public sealed class RawObservationRow
{
    public Guid Id { get; set; }

    /// <summary>Reference to sources.id.</summary>
    public int SourceId { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public string PayloadReference { get; set; } = string.Empty;

    public string? IngestOffset { get; set; }

    public string RawPayload { get; set; } = string.Empty;
}

public sealed class NormalizedEventRow
{
    public Guid Id { get; set; }

    /// <summary>Reference to sources.id; the source's type lives there too.</summary>
    public int SourceId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string EntitiesJson { get; set; } = "[]";

    public string PayloadJson { get; set; } = "{}";

    public Guid RawObservationId { get; set; }
}

public sealed class IncidentRow
{
    public Guid Id { get; set; }

    public string CorrelationKey { get; set; } = string.Empty;

    public DateTimeOffset WindowStart { get; set; }

    public DateTimeOffset WindowEnd { get; set; }

    public string EventIdsJson { get; set; } = "[]";

    public string EvidenceJson { get; set; } = "[]";

    public int State { get; set; }
}

public sealed class ClassificationRow
{
    public Guid Id { get; set; }

    public int SubjectKind { get; set; }

    public Guid SubjectId { get; set; }

    /// <summary>Reference to classifiers.id.</summary>
    public int ClassifierId { get; set; }

    public string? ModelJson { get; set; }

    public string Category { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public int Severity { get; set; }

    public string ReasonsJson { get; set; } = "[]";

    public string? RecommendedAction { get; set; }

    public double? Uncertainty { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DecisionRow
{
    public Guid Id { get; set; }

    public Guid ClassificationId { get; set; }

    /// <summary>Reference to policies.id; the policy version lives there too.</summary>
    public int PolicyId { get; set; }

    public int Outcome { get; set; }

    public string Rationale { get; set; } = string.Empty;

    public string GuardrailsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ActionRecordRow
{
    public Guid Id { get; set; }

    public Guid DecisionId { get; set; }

    /// <summary>Reference to action_providers.id.</summary>
    public int ProviderId { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public string? ParametersJson { get; set; }

    public int Status { get; set; }

    public string? Error { get; set; }

    public string? RollbackJson { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AuditRecordRow
{
    public Guid Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public int Stage { get; set; }

    public string Summary { get; set; } = string.Empty;

    /// <summary>Reference to sources.id, when the record relates to a source.</summary>
    public int? SourceId { get; set; }

    public Guid? EventId { get; set; }

    public Guid? IncidentId { get; set; }

    public Guid? ClassificationId { get; set; }

    public Guid? DecisionId { get; set; }

    public Guid? ActionId { get; set; }

    public string? DetailJson { get; set; }
}

public sealed class CorrectionRow
{
    public Guid Id { get; set; }

    public Guid ClassificationId { get; set; }

    public string CorrectedCategory { get; set; } = string.Empty;

    public string CorrectedBy { get; set; } = string.Empty;

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class QueueTelemetryRow
{
    public string InstanceId { get; set; } = string.Empty;

    public string QueueName { get; set; } = string.Empty;

    public int Depth { get; set; }

    public int InFlight { get; set; }

    public DateTimeOffset? OldestPendingEnqueuedAt { get; set; }

    public long TotalEnqueued { get; set; }

    public long TotalCompleted { get; set; }

    public long TotalAbandoned { get; set; }

    public int DeadLetterCount { get; set; }

    public DateTimeOffset CapturedAt { get; set; }
}

public sealed class SourceOffsetRow
{
    public string SourceId { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class QueueMessageRow
{
    public long Id { get; set; }

    public string QueueName { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public int DeliveryCount { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }

    public DateTimeOffset? LeasedUntil { get; set; }

    public bool DeadLettered { get; set; }
}

public sealed class QueueCounterRow
{
    public string QueueName { get; set; } = string.Empty;

    public long Enqueued { get; set; }

    public long Completed { get; set; }

    public long Abandoned { get; set; }
}
