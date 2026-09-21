using System.Text.Json;
using Viegard.Application.Configuration;
using Viegard.Application.Policy;
using Viegard.Application.Retention;
using Viegard.Domain.Admin;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.Domain.Classifications;
using Viegard.Domain.Configuration;
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
        AuthorizedTargetIp = decision.AuthorizedTargetIp,
        RecommendedActionDuration = decision.RecommendedActionDuration,
        ReviewedBy = decision.ReviewedBy,
        ReviewedAt = Utc(decision.ReviewedAt),
        ReviewOutcome = decision.ReviewOutcome is null ? null : (int)decision.ReviewOutcome,
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
        AuthorizedTargetIp = row.AuthorizedTargetIp,
        RecommendedActionDuration = row.RecommendedActionDuration,
        ReviewedBy = row.ReviewedBy,
        ReviewedAt = row.ReviewedAt,
        ReviewOutcome = row.ReviewOutcome is null ? null : (DecisionReviewOutcome)row.ReviewOutcome,
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
        ResultsJson = action.ResultsJson,
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
        ResultsJson = row.ResultsJson,
        RequestedAt = row.RequestedAt,
        CompletedAt = row.CompletedAt,
    };

    public static ActiveBanRow ToRow(this ActiveBan activeBan) => new()
    {
        Id = activeBan.Id,
        Ip = activeBan.Ip,
        ExpiresAt = Utc(activeBan.ExpiresAt),
        CreatedAt = Utc(activeBan.CreatedAt),
        DecisionId = activeBan.DecisionId,
        ActionId = activeBan.ActionId,
    };

    public static ActiveBan ToDomain(this ActiveBanRow row) =>
        ActiveBan.NormalizeForSave(new ActiveBan
        {
            Id = row.Id,
            Ip = row.Ip,
            ExpiresAt = row.ExpiresAt,
            CreatedAt = row.CreatedAt,
            DecisionId = row.DecisionId,
            ActionId = row.ActionId,
        });

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

    public static InstanceRegistrationRow ToRow(this InstanceRegistration registration) => new()
    {
        InstanceId = registration.InstanceId,
        Version = registration.Version,
        CommitSha = registration.CommitSha,
        Roles = registration.Roles,
        UpgradeTarget = registration.UpgradeTarget,
        HostName = registration.HostName,
        StartedAt = Utc(registration.StartedAt),
        ReportedAt = Utc(registration.ReportedAt),
    };

    public static InstanceRegistration ToDomain(this InstanceRegistrationRow row) => new()
    {
        InstanceId = row.InstanceId,
        Version = row.Version,
        CommitSha = row.CommitSha,
        Roles = row.Roles,
        UpgradeTarget = row.UpgradeTarget,
        HostName = row.HostName,
        StartedAt = row.StartedAt,
        ReportedAt = row.ReportedAt,
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

    public static AdminUserPreferencesRow ToRow(this AdminUserPreferences preferences) => new()
    {
        UserId = preferences.UserId,
        TimeZoneId = preferences.TimeZoneId,
        PageSize = preferences.PageSize,
        StatusRefreshSeconds = preferences.StatusRefreshSeconds,
        UpdatedAt = Utc(preferences.UpdatedAt),
    };

    public static AdminUserPreferences ToDomain(this AdminUserPreferencesRow row) => new()
    {
        UserId = row.UserId,
        TimeZoneId = row.TimeZoneId,
        PageSize = row.PageSize,
        StatusRefreshSeconds = row.StatusRefreshSeconds,
        UpdatedAt = row.UpdatedAt,
    };

    public static AdminWebAuthnCredentialRow ToRow(this AdminWebAuthnCredential credential) => new()
    {
        Id = credential.Id,
        UserId = credential.UserId,
        CredentialId = credential.CredentialId.ToArray(),
        PublicKey = credential.PublicKey.ToArray(),
        SignCount = credential.SignCount,
        Aaguid = credential.Aaguid,
        Transports = credential.Transports,
        Name = credential.Name,
        CreatedAt = Utc(credential.CreatedAt),
        LastUsedAt = Utc(credential.LastUsedAt),
    };

    public static AdminWebAuthnCredential ToDomain(this AdminWebAuthnCredentialRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        CredentialId = row.CredentialId.ToArray(),
        PublicKey = row.PublicKey.ToArray(),
        SignCount = row.SignCount,
        Aaguid = row.Aaguid,
        Transports = row.Transports,
        Name = row.Name,
        CreatedAt = row.CreatedAt,
        LastUsedAt = row.LastUsedAt,
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

    public static CustomSignatureRow ToRow(this CustomSignature signature) => new()
    {
        Id = signature.Id,
        Name = signature.Name,
        Enabled = signature.Enabled,
        Target = (int)signature.Target,
        MatchType = (int)signature.MatchType,
        Pattern = signature.Pattern,
        AdditionalPatternsJson = AdditionalPatternsOrEmpty(signature).Count == 0
            ? null
            : ToJson(AdditionalPatternsOrEmpty(signature)),
        Category = signature.Category,
        Severity = signature.Severity,
        EvidenceWeight = signature.EvidenceWeight,
        CreatedAt = Utc(signature.CreatedAt),
        UpdatedAt = Utc(signature.UpdatedAt),
        UpdatedBy = signature.UpdatedBy,
        Version = signature.Version,
    };

    public static CustomSignature ToDomain(this CustomSignatureRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Enabled = row.Enabled,
        Target = (CustomSignatureTarget)row.Target,
        MatchType = (CustomSignatureMatchType)row.MatchType,
        Pattern = row.Pattern,
        AdditionalPatterns = AdditionalPatternsFromJson(row.AdditionalPatternsJson),
        Category = row.Category,
        Severity = row.Severity,
        EvidenceWeight = row.EvidenceWeight,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
        Version = row.Version,
    };

    public static bool TryToDomain(
        this CustomSignatureRow row,
        out CustomSignature? signature,
        out IReadOnlyList<string> errors)
    {
        try
        {
            signature = row.ToDomain();
            errors = [];
            return true;
        }
        catch (JsonException ex)
        {
            signature = null;
            errors = [$"Additional patterns JSON is invalid: {ex.Message}"];
            return false;
        }
        catch (InvalidOperationException ex)
        {
            signature = null;
            errors = [$"Additional patterns JSON is invalid: {ex.Message}"];
            return false;
        }
    }

    private static IReadOnlyList<string> AdditionalPatternsFromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : FromJson<List<string>>(json);

    private static IReadOnlyList<string> AdditionalPatternsOrEmpty(CustomSignature signature) =>
        signature.AdditionalPatterns ?? [];

    public static HostUpgradeCommandRow ToRow(this HostUpgradeCommand command) => new()
    {
        Id = command.Id,
        Target = command.Target,
        Status = (int)command.Status,
        RequestedAt = Utc(command.RequestedAt),
        RequestedBy = command.RequestedBy,
        StartedAt = Utc(command.StartedAt),
        FinishedAt = Utc(command.FinishedAt),
        Detail = command.Detail,
    };

    public static HostUpgradeCommand ToDomain(this HostUpgradeCommandRow row) => new()
    {
        Id = row.Id,
        Target = row.Target,
        Status = (HostUpgradeCommandStatus)row.Status,
        RequestedAt = row.RequestedAt,
        RequestedBy = row.RequestedBy,
        StartedAt = row.StartedAt,
        FinishedAt = row.FinishedAt,
        Detail = row.Detail,
    };

    public static IngestionFilterRow ToRow(this IngestionFilter filter) => new()
    {
        SourceType = IngestionFilterValidation.NormalizeSourceType(filter.SourceType),
        EventKind = IngestionFilterValidation.NormalizeEventKind(filter.EventKind),
        Suppressed = filter.Suppressed,
        UpdatedAt = Utc(filter.UpdatedAt),
        UpdatedBy = IngestionFilterValidation.NormalizeUpdatedBy(filter.UpdatedBy),
    };

    public static IngestionFilter ToDomain(this IngestionFilterRow row) => new()
    {
        SourceType = row.SourceType,
        EventKind = row.EventKind,
        Suppressed = row.Suppressed,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
    };

    public static RetentionSettingsRow ToRow(this RetentionSettings settings) => new()
    {
        Id = settings.Id,
        RawObservationsDays = settings.RawObservationsDays,
        EventsDays = settings.EventsDays,
        IncidentsDays = settings.IncidentsDays,
        ClassificationsDays = settings.ClassificationsDays,
        DecisionsDays = settings.DecisionsDays,
        ActionsDays = settings.ActionsDays,
        AuditRecordsDays = settings.AuditRecordsDays,
        DeadLetteredQueueMessagesDays = settings.DeadLetteredQueueMessagesDays,
        ExpiredAdminSessionsDays = settings.ExpiredAdminSessionsDays,
        LocalModelAdvisorConsultsDays = settings.LocalModelAdvisorConsultsDays,
        Version = settings.Version,
        SeededAt = Utc(settings.SeededAt),
        UpdatedAt = Utc(settings.UpdatedAt),
        UpdatedBy = settings.UpdatedBy,
        LastCycleAt = Utc(settings.LastCycleAt),
        LastCycleCountsJson = settings.LastCycleCountsJson,
    };

    public static RetentionSettings ToDomain(this RetentionSettingsRow row) => new()
    {
        Id = row.Id,
        RawObservationsDays = row.RawObservationsDays,
        EventsDays = row.EventsDays,
        IncidentsDays = row.IncidentsDays,
        ClassificationsDays = row.ClassificationsDays,
        DecisionsDays = row.DecisionsDays,
        ActionsDays = row.ActionsDays,
        AuditRecordsDays = row.AuditRecordsDays,
        DeadLetteredQueueMessagesDays = row.DeadLetteredQueueMessagesDays,
        ExpiredAdminSessionsDays = row.ExpiredAdminSessionsDays,
        LocalModelAdvisorConsultsDays = row.LocalModelAdvisorConsultsDays,
        Version = row.Version,
        SeededAt = row.SeededAt,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
        LastCycleAt = row.LastCycleAt,
        LastCycleCountsJson = row.LastCycleCountsJson,
    };

    public static JetPackFeedSettingsRow ToRow(this JetPackFeedSettings settings) => new()
    {
        Id = settings.Id,
        FeedUrl = settings.FeedUrl,
        FetchInterval = settings.FetchInterval,
        Enabled = settings.Enabled,
        AddressListName = settings.AddressListName,
        Version = settings.Version,
        SeededAt = Utc(settings.SeededAt),
        UpdatedAt = Utc(settings.UpdatedAt),
        UpdatedBy = settings.UpdatedBy,
    };

    public static JetPackFeedSettings ToDomain(this JetPackFeedSettingsRow row) => new()
    {
        Id = row.Id,
        FeedUrl = row.FeedUrl,
        FetchInterval = row.FetchInterval,
        Enabled = row.Enabled,
        AddressListName = row.AddressListName,
        Version = row.Version,
        SeededAt = row.SeededAt,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
    };

    public static JetPackDesiredAddressRow ToRow(this JetPackDesiredAddress address) => new()
    {
        Address = address.Address,
        FirstSeenAt = Utc(address.FirstSeenAt),
        LastSeenAt = Utc(address.LastSeenAt),
    };

    public static JetPackDesiredAddress ToDomain(this JetPackDesiredAddressRow row) => new()
    {
        Address = row.Address,
        FirstSeenAt = row.FirstSeenAt,
        LastSeenAt = row.LastSeenAt,
    };

    public static LocalModelAdvisorSettingsRow ToRow(this LocalModelAdvisorSettings settings) => new()
    {
        Id = settings.Id,
        Enabled = settings.Enabled,
        Endpoint = settings.Endpoint,
        Model = settings.Model,
        Temperature = settings.Temperature,
        TimeoutMs = settings.TimeoutMs,
        KeepAlive = settings.KeepAlive,
        InvokeConfidenceMin = settings.InvokeConfidenceMin,
        InvokeConfidenceMax = settings.InvokeConfidenceMax,
        MaxSeverityDelta = settings.MaxSeverityDelta,
        MaxConfidenceDelta = settings.MaxConfidenceDelta,
        ResponseCacheEnabled = settings.ResponseCacheEnabled,
        ResponseCacheTtlHours = settings.ResponseCacheTtlHours,
        EnsembleEnabled = settings.EnsembleEnabled,
        SecondModelEndpoint = settings.SecondModelEndpoint,
        SecondModel = settings.SecondModel,
        InjectionAction = (int)settings.InjectionAction,
        DeEscalationEnabled = settings.DeEscalationEnabled,
        MaxDownwardSeverityDelta = settings.MaxDownwardSeverityDelta,
        MaxDownwardConfidenceDelta = settings.MaxDownwardConfidenceDelta,
        DeEscalationMinModelConfidence = settings.DeEscalationMinModelConfidence,
        DeEscalationProtectedSeverity = settings.DeEscalationProtectedSeverity,
        Version = settings.Version,
        SeededAt = Utc(settings.SeededAt),
        UpdatedAt = Utc(settings.UpdatedAt),
        UpdatedBy = settings.UpdatedBy,
    };

    public static LocalModelAdvisorSettings ToDomain(this LocalModelAdvisorSettingsRow row) => new()
    {
        Id = row.Id,
        Enabled = row.Enabled,
        Endpoint = row.Endpoint,
        Model = row.Model,
        Temperature = row.Temperature,
        TimeoutMs = row.TimeoutMs,
        KeepAlive = row.KeepAlive,
        InvokeConfidenceMin = row.InvokeConfidenceMin,
        InvokeConfidenceMax = row.InvokeConfidenceMax,
        MaxSeverityDelta = row.MaxSeverityDelta,
        MaxConfidenceDelta = row.MaxConfidenceDelta,
        ResponseCacheEnabled = row.ResponseCacheEnabled,
        ResponseCacheTtlHours = row.ResponseCacheTtlHours,
        EnsembleEnabled = row.EnsembleEnabled,
        SecondModelEndpoint = row.SecondModelEndpoint,
        SecondModel = row.SecondModel,
        InjectionAction = (AdvisorInjectionAction)row.InjectionAction,
        DeEscalationEnabled = row.DeEscalationEnabled,
        MaxDownwardSeverityDelta = row.MaxDownwardSeverityDelta,
        MaxDownwardConfidenceDelta = row.MaxDownwardConfidenceDelta,
        DeEscalationMinModelConfidence = row.DeEscalationMinModelConfidence,
        DeEscalationProtectedSeverity = row.DeEscalationProtectedSeverity,
        Version = row.Version,
        SeededAt = row.SeededAt,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
    };

    public static LocalModelAdvisorCategoryBandRow ToRow(this LocalModelAdvisorCategoryBand band) => new()
    {
        Category = band.Category,
        Enabled = band.Enabled,
        InvokeConfidenceMin = band.InvokeConfidenceMin,
        InvokeConfidenceMax = band.InvokeConfidenceMax,
        MaxSeverityDelta = band.MaxSeverityDelta,
        MaxConfidenceDelta = band.MaxConfidenceDelta,
        DeEscalationEnabled = band.DeEscalationEnabled,
        MaxDownwardSeverityDelta = band.MaxDownwardSeverityDelta,
        MaxDownwardConfidenceDelta = band.MaxDownwardConfidenceDelta,
        Version = band.Version,
        UpdatedAt = Utc(band.UpdatedAt),
        UpdatedBy = band.UpdatedBy,
    };

    public static LocalModelAdvisorCategoryBand ToDomain(this LocalModelAdvisorCategoryBandRow row) => new()
    {
        Category = row.Category,
        Enabled = row.Enabled,
        InvokeConfidenceMin = row.InvokeConfidenceMin,
        InvokeConfidenceMax = row.InvokeConfidenceMax,
        MaxSeverityDelta = row.MaxSeverityDelta,
        MaxConfidenceDelta = row.MaxConfidenceDelta,
        DeEscalationEnabled = row.DeEscalationEnabled,
        MaxDownwardSeverityDelta = row.MaxDownwardSeverityDelta,
        MaxDownwardConfidenceDelta = row.MaxDownwardConfidenceDelta,
        Version = row.Version,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
    };


    public static LocalModelAdvisorInjectionPatternRow ToRow(this LocalModelAdvisorInjectionPattern pattern) => new()
    {
        Id = pattern.Id,
        Category = pattern.Category,
        Pattern = pattern.Pattern,
        Description = pattern.Description,
        Enabled = pattern.Enabled,
        CreatedAt = Utc(pattern.CreatedAt),
        CreatedBy = pattern.CreatedBy,
    };

    public static LocalModelAdvisorInjectionPattern ToDomain(this LocalModelAdvisorInjectionPatternRow row) => new()
    {
        Id = row.Id,
        Category = row.Category,
        Pattern = row.Pattern,
        Description = row.Description,
        Enabled = row.Enabled,
        CreatedAt = row.CreatedAt,
        CreatedBy = row.CreatedBy,
    };


    public static LocalModelAdvisorPromptTemplateRow ToRow(this LocalModelAdvisorPromptTemplateRevision revision) => new()
    {
        Id = revision.Id,
        TemplateId = revision.TemplateId,
        Revision = revision.Revision,
        SystemInstructions = revision.SystemInstructions,
        ApplicationInstructions = revision.ApplicationInstructions,
        IsActive = revision.IsActive,
        Note = revision.Note,
        CreatedAt = Utc(revision.CreatedAt),
        CreatedBy = revision.CreatedBy,
    };

    public static LocalModelAdvisorPromptTemplateRevision ToDomain(this LocalModelAdvisorPromptTemplateRow row) => new()
    {
        Id = row.Id,
        TemplateId = row.TemplateId,
        Revision = row.Revision,
        SystemInstructions = row.SystemInstructions,
        ApplicationInstructions = row.ApplicationInstructions,
        IsActive = row.IsActive,
        Note = row.Note,
        CreatedAt = row.CreatedAt,
        CreatedBy = row.CreatedBy,
    };

    public static LocalModelAdvisorResponseCacheRow ToRow(this LocalModelAdvisorResponseCacheEntry entry) => new()
    {
        Id = entry.Id,
        CacheKey = entry.CacheKey,
        ModelId = entry.ModelId,
        TemplateVersion = entry.TemplateVersion,
        Severity = entry.Severity,
        Confidence = entry.Confidence,
        ReasonsJson = ToJson(entry.Reasons),
        CreatedAt = Utc(entry.CreatedAt),
        ExpiresAt = Utc(entry.ExpiresAt),
    };

    public static LocalModelAdvisorResponseCacheEntry ToDomain(this LocalModelAdvisorResponseCacheRow row) => new()
    {
        Id = row.Id,
        CacheKey = row.CacheKey,
        ModelId = row.ModelId,
        TemplateVersion = row.TemplateVersion,
        Severity = row.Severity,
        Confidence = row.Confidence,
        Reasons = FromJson<List<string>>(row.ReasonsJson),
        CreatedAt = row.CreatedAt,
        ExpiresAt = row.ExpiresAt,
    };

    public static LocalModelAdvisorConsultRow ToRow(this AdvisorConsultRecord record) => new()
    {
        Id = record.Id,
        ClassificationId = record.ClassificationId,
        IncidentId = record.IncidentId,
        Category = record.Category,
        Outcome = (int)record.Outcome,
        BaseSeverity = record.BaseSeverity,
        FinalSeverity = record.FinalSeverity,
        BaseConfidence = record.BaseConfidence,
        FinalConfidence = record.FinalConfidence,
        LatencyMs = record.LatencyMs,
        FailureKind = record.FailureKind,
        ModelId = record.ModelId,
        ServedFromCache = record.ServedFromCache,
        EnsembleDetailJson = record.EnsembleDetail is null ? null : ToJson(record.EnsembleDetail),
        InjectionDetected = record.InjectionDetected,
        InjectionCategoriesJson = ToJson(record.InjectionCategories),
        AdvisorSkippedForInjection = record.AdvisorSkippedForInjection,
        CreatedAt = Utc(record.CreatedAt),
    };

    public static AdvisorConsultRecord ToDomain(this LocalModelAdvisorConsultRow row) => new()
    {
        Id = row.Id,
        ClassificationId = row.ClassificationId,
        IncidentId = row.IncidentId,
        Category = row.Category,
        Outcome = (AdvisorConsultOutcome)row.Outcome,
        BaseSeverity = row.BaseSeverity,
        FinalSeverity = row.FinalSeverity,
        BaseConfidence = row.BaseConfidence,
        FinalConfidence = row.FinalConfidence,
        LatencyMs = row.LatencyMs,
        FailureKind = row.FailureKind,
        ModelId = row.ModelId,
        ServedFromCache = row.ServedFromCache,
        EnsembleDetail = row.EnsembleDetailJson is null ? null : FromJson<AdvisorEnsembleDetail>(row.EnsembleDetailJson),
        InjectionDetected = row.InjectionDetected,
        InjectionCategories = FromJson<List<string>>(row.InjectionCategoriesJson),
        AdvisorSkippedForInjection = row.AdvisorSkippedForInjection,
        CreatedAt = row.CreatedAt,
    };

    public static PolicyThresholdSettingsRow ToRow(this PolicyThresholdSettings settings) => new()
    {
        Id = settings.Id,
        ReviewConfidence = settings.ReviewConfidence,
        ActionConfidence = settings.ActionConfidence,
        ActionMinSeverity = settings.ActionMinSeverity,
        RowVersion = settings.RowVersion,
        UpdatedAt = Utc(settings.UpdatedAt),
        UpdatedBy = settings.UpdatedBy,
    };

    public static PolicyThresholdSettings ToDomain(this PolicyThresholdSettingsRow row) => new()
    {
        Id = row.Id,
        ReviewConfidence = row.ReviewConfidence,
        ActionConfidence = row.ActionConfidence,
        ActionMinSeverity = row.ActionMinSeverity,
        RowVersion = row.RowVersion,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
    };

    public static PolicyPostureSettingsRow ToRow(this PolicyPostureSettings settings) => new()
    {
        Id = settings.Id,
        DryRun = settings.DryRun,
        ManualApprovalMode = settings.ManualApprovalMode,
        EmergencyStop = settings.EmergencyStop,
        RowVersion = settings.RowVersion,
        UpdatedAt = Utc(settings.UpdatedAt),
        UpdatedBy = settings.UpdatedBy,
    };

    public static PolicyPostureSettings ToDomain(this PolicyPostureSettingsRow row) => new()
    {
        Id = row.Id,
        DryRun = row.DryRun,
        ManualApprovalMode = row.ManualApprovalMode,
        EmergencyStop = row.EmergencyStop,
        RowVersion = row.RowVersion,
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy,
    };

    public static AdminErrorRow ToRow(this AdminError error) => new()
    {
        Id = error.Id,
        OccurredAt = Utc(error.OccurredAt),
        RequestId = error.RequestId,
        Path = error.Path,
        Method = error.Method,
        Username = error.Username,
        ExceptionType = error.ExceptionType,
        Message = error.Message,
        StackTrace = error.StackTrace,
    };

    public static AdminError ToDomain(this AdminErrorRow row) => new()
    {
        Id = row.Id,
        OccurredAt = row.OccurredAt,
        RequestId = row.RequestId,
        Path = row.Path,
        Method = row.Method,
        Username = row.Username,
        ExceptionType = row.ExceptionType,
        Message = row.Message,
        StackTrace = row.StackTrace,
    };

    public static MikroTikRouterRow ToRow(this MikroTikRouter router) => new()
    {
        Id = router.Id,
        Name = router.Name,
        BaseUrl = router.BaseUrl,
        TransportMode = router.TransportMode.ToString(),
        PinnedCertificateSha256 = router.PinnedCertificateSha256,
        Username = router.Username,
        Enabled = router.Enabled,
        CreatedAt = Utc(router.CreatedAt),
        UpdatedAt = Utc(router.UpdatedAt),
        UpdatedBy = router.UpdatedBy,
        RowVersion = router.RowVersion,
    };

    public static MikroTikRouter ToDomain(this MikroTikRouterRow row)
    {
        if (!Enum.TryParse<MikroTikRouterTransportMode>(row.TransportMode, ignoreCase: false, out var transportMode)
            || !Enum.IsDefined(transportMode))
        {
            throw new InvalidOperationException($"Persisted MikroTik router transport mode '{row.TransportMode}' is not supported.");
        }

        var router = new MikroTikRouter
        {
            Id = row.Id,
            Name = row.Name,
            BaseUrl = row.BaseUrl,
            TransportMode = transportMode,
            PinnedCertificateSha256 = row.PinnedCertificateSha256,
            Username = row.Username,
            Enabled = row.Enabled,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            UpdatedBy = row.UpdatedBy,
            RowVersion = row.RowVersion,
        };

        return MikroTikRouterValidator.NormalizeForSave(router);
    }

    public static AuditRecord ToDomain(this AuditRecordRow row, string? sourceKey) => new()
    {
        Id = row.Id,
        Timestamp = row.Timestamp,
        Stage = (PipelineStage)row.Stage,
        Summary = row.Summary,
        SourceId = sourceKey,
        EventId = row.EventId,
        IncidentId = row.IncidentId,
        ClassificationId = row.ClassificationId,
        DecisionId = row.DecisionId,
        ActionId = row.ActionId,
        DetailJson = row.DetailJson,
    };
}
