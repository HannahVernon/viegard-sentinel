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

    public string? AuthorizedTargetIp { get; set; }

    public TimeSpan? RecommendedActionDuration { get; set; }

    public string? ReviewedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public int? ReviewOutcome { get; set; }
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

    public string? ResultsJson { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ActiveBanRow
{
    public Guid Id { get; set; }

    public string Ip { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid DecisionId { get; set; }

    public Guid ActionId { get; set; }
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

public sealed class InstanceRegistrationRow
{
    public string InstanceId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string? CommitSha { get; set; }

    public string Roles { get; set; } = string.Empty;

    public string? UpgradeTarget { get; set; }

    public string HostName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset ReportedAt { get; set; }
}

public sealed class SourceOffsetRow
{
    public string SourceId { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class CustomSignatureRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public int Target { get; set; }

    public int MatchType { get; set; }

    public string Pattern { get; set; } = string.Empty;

    public string? AdditionalPatternsJson { get; set; }

    public string Category { get; set; } = string.Empty;

    public int Severity { get; set; }

    public double EvidenceWeight { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public int Version { get; set; }
}

public sealed class HostUpgradeCommandRow
{
    public Guid Id { get; set; }

    public string Target { get; set; } = string.Empty;

    public int Status { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public string RequestedBy { get; set; } = string.Empty;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Detail { get; set; }
}

public sealed class IngestionFilterRow
{
    public string SourceType { get; set; } = string.Empty;

    public string EventKind { get; set; } = string.Empty;

    public bool Suppressed { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class RetentionSettingsRow
{
    public int Id { get; set; }

    public int? RawObservationsDays { get; set; }

    public int? EventsDays { get; set; }

    public int? IncidentsDays { get; set; }

    public int? ClassificationsDays { get; set; }

    public int? DecisionsDays { get; set; }

    public int? ActionsDays { get; set; }

    public int? AuditRecordsDays { get; set; }

    public int? DeadLetteredQueueMessagesDays { get; set; }

    public int? ExpiredAdminSessionsDays { get; set; }

    public int? LocalModelAdvisorConsultsDays { get; set; }

    public int Version { get; set; }

    public DateTimeOffset? SeededAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public DateTimeOffset? LastCycleAt { get; set; }

    public string? LastCycleCountsJson { get; set; }
}

public sealed class JetPackFeedSettingsRow
{
    public int Id { get; set; }

    public string FeedUrl { get; set; } = string.Empty;

    public TimeSpan FetchInterval { get; set; }

    public bool Enabled { get; set; }

    public string AddressListName { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateTimeOffset? SeededAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class JetPackDesiredAddressRow
{
    public string Address { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class LocalModelAdvisorSettingsRow
{
    public int Id { get; set; }

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public double Temperature { get; set; }

    public int TimeoutMs { get; set; }

    public string KeepAlive { get; set; } = string.Empty;

    public double InvokeConfidenceMin { get; set; }

    public double InvokeConfidenceMax { get; set; }

    public int MaxSeverityDelta { get; set; }

    public double MaxConfidenceDelta { get; set; }

    public bool ResponseCacheEnabled { get; set; }

    public int ResponseCacheTtlHours { get; set; }

    public bool EnsembleEnabled { get; set; }

    public string SecondModelEndpoint { get; set; } = string.Empty;

    public string SecondModel { get; set; } = string.Empty;

    public int InjectionAction { get; set; }

    public bool DeEscalationEnabled { get; set; }

    public int MaxDownwardSeverityDelta { get; set; }

    public double MaxDownwardConfidenceDelta { get; set; }

    public double DeEscalationMinModelConfidence { get; set; }

    public int DeEscalationProtectedSeverity { get; set; }

    public int Version { get; set; }

    public DateTimeOffset? SeededAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class LocalModelAdvisorCategoryBandRow
{
    public string Category { get; set; } = string.Empty;

    public bool? Enabled { get; set; }

    public double? InvokeConfidenceMin { get; set; }

    public double? InvokeConfidenceMax { get; set; }

    public int? MaxSeverityDelta { get; set; }

    public double? MaxConfidenceDelta { get; set; }

    public bool? DeEscalationEnabled { get; set; }

    public int? MaxDownwardSeverityDelta { get; set; }

    public double? MaxDownwardConfidenceDelta { get; set; }

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class LocalModelAdvisorInjectionPatternRow
{
    public Guid Id { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class LocalModelAdvisorPromptTemplateRow
{
    public Guid Id { get; set; }

    public string TemplateId { get; set; } = string.Empty;

    public int Revision { get; set; }

    public string SystemInstructions { get; set; } = string.Empty;

    public string ApplicationInstructions { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class LocalModelAdvisorResponseCacheRow
{
    public Guid Id { get; set; }

    public string CacheKey { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string TemplateVersion { get; set; } = string.Empty;

    public int Severity { get; set; }

    public double Confidence { get; set; }

    public string ReasonsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class LocalModelAdvisorConsultRow
{
    public Guid Id { get; set; }

    public Guid ClassificationId { get; set; }

    public Guid IncidentId { get; set; }

    public string Category { get; set; } = string.Empty;

    public int Outcome { get; set; }

    public int BaseSeverity { get; set; }

    public int FinalSeverity { get; set; }

    public double BaseConfidence { get; set; }

    public double FinalConfidence { get; set; }

    public int? LatencyMs { get; set; }

    public string? FailureKind { get; set; }

    public string? ModelId { get; set; }

    public bool ServedFromCache { get; set; }

    public string? EnsembleDetailJson { get; set; }

    public bool InjectionDetected { get; set; }

    public string InjectionCategoriesJson { get; set; } = "[]";

    public bool AdvisorSkippedForInjection { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PolicyThresholdSettingsRow
{
    public int Id { get; set; }

    public double ReviewConfidence { get; set; }

    public double ActionConfidence { get; set; }

    public int ActionMinSeverity { get; set; }

    public int RowVersion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class PolicyPostureSettingsRow
{
    public int Id { get; set; }

    public bool DryRun { get; set; }

    public bool ManualApprovalMode { get; set; }

    public bool EmergencyStop { get; set; }

    public int RowVersion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class AdminErrorRow
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string RequestId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string ExceptionType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string StackTrace { get; set; } = string.Empty;
}

public sealed class MikroTikRouterRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string TransportMode { get; set; } = string.Empty;

    public string? PinnedCertificateSha256 { get; set; }

    public string Username { get; set; } = string.Empty;

    public string PasswordCiphertext { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public int RowVersion { get; set; }
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

public sealed class AdminUserRow
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTimeOffset PasswordChangedAt { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public bool MustChangePassword { get; set; }

    public bool TotpEnrolled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AdminTotpSecretRow
{
    public Guid UserId { get; set; }

    public string SecretBase32 { get; set; } = string.Empty;

    public long? LastAcceptedStep { get; set; }

    public DateTimeOffset EnrolledAt { get; set; }
}

public sealed class AdminRecoveryCodeRow
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AdminWebAuthnCredentialRow
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public byte[] CredentialId { get; set; } = [];

    public byte[] PublicKey { get; set; } = [];

    public long SignCount { get; set; }

    public Guid Aaguid { get; set; }

    public string? Transports { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class AdminUserPreferencesRow
{
    public Guid UserId { get; set; }

    public string TimeZoneId { get; set; } = "UTC";

    public int PageSize { get; set; } = 50;

    public int StatusRefreshSeconds { get; set; } = 30;

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppPasswordRow
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string LookupKey { get; set; } = string.Empty;

    public string SecretHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AdminSessionRow
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    public DateTimeOffset IdleExpiresAt { get; set; }

    public string Ip { get; set; } = string.Empty;

    public string IpBindingMode { get; set; } = string.Empty;

    public string UserAgent { get; set; } = string.Empty;

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? StepUpAt { get; set; }
}
