using System.Text.Json;
using Viegard.Domain.Admin;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Feedback;
using Viegard.Domain.Health;
using Viegard.Domain.Incidents;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres;

/// <summary>Domain &lt;-&gt; row mapping.  JSON columns use web-default serializer options.</summary>
internal static class Mapping
{
    /// <summary>
    /// AllowOutOfOrderMetadataProperties is required because PostgreSQL
    /// jsonb does not preserve key order: the $payloadType discriminator is
    /// not guaranteed to arrive first when payloads are read back.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new InvalidOperationException($"Persisted JSON deserialized to null for {typeof(T).Name}.");

    /// <summary>
    /// Npgsql only accepts offset-zero DateTimeOffset values for
    /// timestamptz; observed timestamps (e.g., nginx "-0500") arrive with
    /// local offsets.  Normalizing preserves the instant exactly.
    /// </summary>
    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    private static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();

    public static RawObservationRow ToRow(this RawObservation observation, string rawPayload, int sourceRefId) => new()
    {
        Id = observation.Id,
        SourceId = sourceRefId,
        ObservedAt = Utc(observation.ObservedAt),
        PayloadReference = observation.PayloadReference,
        IngestOffset = observation.IngestOffset,
        RawPayload = rawPayload,
    };

    public static RawObservation ToDomain(this RawObservationRow row, string sourceKey, string sourceType) => new()
    {
        Id = row.Id,
        SourceId = sourceKey,
        SourceType = sourceType,
        ObservedAt = row.ObservedAt,
        PayloadReference = row.PayloadReference,
        IngestOffset = row.IngestOffset,
    };

    public static NormalizedEventRow ToRow(this NormalizedEvent normalizedEvent, int sourceRefId) => new()
    {
        Id = normalizedEvent.Id,
        SourceId = sourceRefId,
        OccurredAt = Utc(normalizedEvent.OccurredAt),
        EntitiesJson = ToJson(normalizedEvent.Entities),
        PayloadJson = ToJson(normalizedEvent.Payload),
        RawObservationId = normalizedEvent.RawObservationId,
    };

    public static NormalizedEvent ToDomain(this NormalizedEventRow row, string sourceKey, string sourceType) => new()
    {
        Id = row.Id,
        SourceId = sourceKey,
        SourceType = sourceType,
        OccurredAt = row.OccurredAt,
        Entities = FromJson<List<EntityRef>>(row.EntitiesJson),
        Payload = FromJson<EventPayload>(row.PayloadJson),
        RawObservationId = row.RawObservationId,
    };

    public static IncidentRow ToRow(this Incident incident) => new()
    {
        Id = incident.Id,
        CorrelationKey = incident.CorrelationKey,
        WindowStart = Utc(incident.WindowStart),
        WindowEnd = Utc(incident.WindowEnd),
        EventIdsJson = ToJson(incident.EventIds),
        EvidenceJson = ToJson(incident.Evidence),
        State = (int)incident.State,
    };

    public static Incident ToDomain(this IncidentRow row) => new()
    {
        Id = row.Id,
        CorrelationKey = row.CorrelationKey,
        WindowStart = row.WindowStart,
        WindowEnd = row.WindowEnd,
        EventIds = FromJson<List<Guid>>(row.EventIdsJson),
        Evidence = FromJson<List<EvidenceItem>>(row.EvidenceJson),
        State = (IncidentState)row.State,
    };

    public static ClassificationRow ToRow(this Classification classification, int classifierRefId) => new()
    {
        Id = classification.Id,
        SubjectKind = (int)classification.SubjectKind,
        SubjectId = classification.SubjectId,
        ClassifierId = classifierRefId,
        ModelJson = classification.Model is null ? null : ToJson(classification.Model),
        Category = classification.Category,
        Confidence = classification.Confidence,
        Severity = classification.Severity,
        ReasonsJson = ToJson(classification.Reasons),
        RecommendedAction = classification.RecommendedAction,
        Uncertainty = classification.Uncertainty,
        CreatedAt = Utc(classification.CreatedAt),
    };

    public static Classification ToDomain(this ClassificationRow row, string classifierKey) => new()
    {
        Id = row.Id,
        SubjectKind = (ClassificationSubjectKind)row.SubjectKind,
        SubjectId = row.SubjectId,
        ClassifierId = classifierKey,
        Model = row.ModelJson is null ? null : FromJson<ModelInfo>(row.ModelJson),
        Category = row.Category,
        Confidence = row.Confidence,
        Severity = row.Severity,
        Reasons = FromJson<List<string>>(row.ReasonsJson),
        RecommendedAction = row.RecommendedAction,
        Uncertainty = row.Uncertainty,
        CreatedAt = row.CreatedAt,
    };

    public static DecisionRow ToRow(this Decision decision, int policyRefId) => new()
    {
        Id = decision.Id,
        ClassificationId = decision.ClassificationId,
        PolicyId = policyRefId,
        Outcome = (int)decision.Outcome,
        Rationale = decision.Rationale,
        GuardrailsJson = ToJson(decision.Guardrails),
        CreatedAt = Utc(decision.CreatedAt),
    };

    public static Decision ToDomain(this DecisionRow row, string policyKey, string policyVersion) => new()
    {
        Id = row.Id,
        ClassificationId = row.ClassificationId,
        PolicyId = policyKey,
        PolicyVersion = policyVersion,
        Outcome = (DecisionOutcome)row.Outcome,
        Rationale = row.Rationale,
        Guardrails = FromJson<List<GuardrailEvaluation>>(row.GuardrailsJson),
        CreatedAt = row.CreatedAt,
    };

    public static ActionRecordRow ToRow(this ActionRecord action, int providerRefId) => new()
    {
        Id = action.Id,
        DecisionId = action.DecisionId,
        ProviderId = providerRefId,
        OperationId = action.OperationId,
        ParametersJson = action.ParametersJson,
        Status = (int)action.Status,
        Error = action.Error,
        RollbackJson = action.RollbackJson,
        RequestedAt = Utc(action.RequestedAt),
        CompletedAt = Utc(action.CompletedAt),
    };

    public static ActionRecord ToDomain(this ActionRecordRow row, string providerKey) => new()
    {
        Id = row.Id,
        DecisionId = row.DecisionId,
        ProviderId = providerKey,
        OperationId = row.OperationId,
        ParametersJson = row.ParametersJson,
        Status = (ActionStatus)row.Status,
        Error = row.Error,
        RollbackJson = row.RollbackJson,
        RequestedAt = row.RequestedAt,
        CompletedAt = row.CompletedAt,
    };

    public static AuditRecordRow ToRow(this AuditRecord record, int? sourceRefId) => new()
    {
        Id = record.Id,
        Timestamp = Utc(record.Timestamp),
        Stage = (int)record.Stage,
        Summary = record.Summary,
        SourceId = sourceRefId,
        EventId = record.EventId,
        IncidentId = record.IncidentId,
        ClassificationId = record.ClassificationId,
        DecisionId = record.DecisionId,
        ActionId = record.ActionId,
        DetailJson = record.DetailJson,
    };

    public static CorrectionRow ToRow(this Correction correction) => new()
    {
        Id = correction.Id,
        ClassificationId = correction.ClassificationId,
        CorrectedCategory = correction.CorrectedCategory,
        CorrectedBy = correction.CorrectedBy,
        Note = correction.Note,
        CreatedAt = Utc(correction.CreatedAt),
    };

    public static Correction ToDomain(this CorrectionRow row) => new()
    {
        Id = row.Id,
        ClassificationId = row.ClassificationId,
        CorrectedCategory = row.CorrectedCategory,
        CorrectedBy = row.CorrectedBy,
        Note = row.Note,
        CreatedAt = row.CreatedAt,
    };

    public static QueueTelemetryRow ToRow(this QueueTelemetrySnapshot snapshot) => new()
    {
        InstanceId = snapshot.InstanceId,
        QueueName = snapshot.QueueName,
        Depth = snapshot.Depth,
        InFlight = snapshot.InFlight,
        OldestPendingEnqueuedAt = Utc(snapshot.OldestPendingEnqueuedAt),
        TotalEnqueued = snapshot.TotalEnqueued,
        TotalCompleted = snapshot.TotalCompleted,
        TotalAbandoned = snapshot.TotalAbandoned,
        DeadLetterCount = snapshot.DeadLetterCount,
        CapturedAt = Utc(snapshot.CapturedAt),
    };

    public static QueueTelemetrySnapshot ToDomain(this QueueTelemetryRow row) => new()
    {
        InstanceId = row.InstanceId,
        QueueName = row.QueueName,
        Depth = row.Depth,
        InFlight = row.InFlight,
        OldestPendingEnqueuedAt = row.OldestPendingEnqueuedAt,
        TotalEnqueued = row.TotalEnqueued,
        TotalCompleted = row.TotalCompleted,
        TotalAbandoned = row.TotalAbandoned,
        DeadLetterCount = row.DeadLetterCount,
        CapturedAt = row.CapturedAt,
    };

    public static AdminUserRow ToRow(this AdminUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        PasswordHash = user.PasswordHash,
        PasswordChangedAt = Utc(user.PasswordChangedAt),
        FailedLoginCount = user.FailedLoginCount,
        LockedUntil = Utc(user.LockedUntil),
        MustChangePassword = user.MustChangePassword,
        TotpEnrolled = user.TotpEnrolled,
        CreatedAt = Utc(user.CreatedAt),
    };

    public static AdminUser ToDomain(this AdminUserRow row) => new()
    {
        Id = row.Id,
        Username = row.Username,
        PasswordHash = row.PasswordHash,
        PasswordChangedAt = row.PasswordChangedAt,
        FailedLoginCount = row.FailedLoginCount,
        LockedUntil = row.LockedUntil,
        MustChangePassword = row.MustChangePassword,
        TotpEnrolled = row.TotpEnrolled,
        CreatedAt = row.CreatedAt,
    };

    public static AdminTotpSecretRow ToRow(this AdminTotpSecret secret) => new()
    {
        UserId = secret.UserId,
        SecretBase32 = secret.SecretBase32,
        LastAcceptedStep = secret.LastAcceptedStep,
        EnrolledAt = Utc(secret.EnrolledAt),
    };

    public static AdminTotpSecret ToDomain(this AdminTotpSecretRow row) => new()
    {
        UserId = row.UserId,
        SecretBase32 = row.SecretBase32,
        LastAcceptedStep = row.LastAcceptedStep,
        EnrolledAt = row.EnrolledAt,
    };

    public static AdminRecoveryCodeRow ToRow(this AdminRecoveryCode code) => new()
    {
        Id = code.Id,
        UserId = code.UserId,
        CodeHash = code.CodeHash,
        UsedAt = Utc(code.UsedAt),
        CreatedAt = Utc(code.CreatedAt),
    };

    public static AdminRecoveryCode ToDomain(this AdminRecoveryCodeRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        CodeHash = row.CodeHash,
        UsedAt = row.UsedAt,
        CreatedAt = row.CreatedAt,
    };

    public static AdminSessionRow ToRow(this AdminSession session) => new()
    {
        Id = session.Id,
        UserId = session.UserId,
        CreatedAt = Utc(session.CreatedAt),
        LastSeenAt = Utc(session.LastSeenAt),
        AbsoluteExpiresAt = Utc(session.AbsoluteExpiresAt),
        IdleExpiresAt = Utc(session.IdleExpiresAt),
        Ip = session.Ip,
        IpBindingMode = session.IpBindingMode,
        UserAgent = session.UserAgent,
        RevokedAt = Utc(session.RevokedAt),
        StepUpAt = Utc(session.StepUpAt),
    };

    public static AdminSession ToDomain(this AdminSessionRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        CreatedAt = row.CreatedAt,
        LastSeenAt = row.LastSeenAt,
        AbsoluteExpiresAt = row.AbsoluteExpiresAt,
        IdleExpiresAt = row.IdleExpiresAt,
        Ip = row.Ip,
        IpBindingMode = row.IpBindingMode,
        UserAgent = row.UserAgent,
        RevokedAt = row.RevokedAt,
        StepUpAt = row.StepUpAt,
    };
}
