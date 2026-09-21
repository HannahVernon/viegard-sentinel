using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres;

/// <summary>
/// EF Core context for Viegard's PostgreSQL persistence (D-0024).  The model
/// is deliberately schema-agnostic: all objects follow the connection's
/// search path, so the schema is deployment configuration
/// (Viegard:Database:Schema), not a compiled-in constant.
/// </summary>
public sealed partial class ViegardDbContext(DbContextOptions<ViegardDbContext> options) : DbContext(options)
{

    public DbSet<RawObservationRow> RawObservations => Set<RawObservationRow>();

    public DbSet<SourceRow> Sources => Set<SourceRow>();

    public DbSet<ClassifierRow> Classifiers => Set<ClassifierRow>();

    public DbSet<PolicyRow> Policies => Set<PolicyRow>();

    public DbSet<ActionProviderRow> ActionProviders => Set<ActionProviderRow>();

    public DbSet<NormalizedEventRow> Events => Set<NormalizedEventRow>();

    public DbSet<IncidentRow> Incidents => Set<IncidentRow>();

    public DbSet<ClassificationRow> Classifications => Set<ClassificationRow>();

    public DbSet<DecisionRow> Decisions => Set<DecisionRow>();

    public DbSet<ActionRecordRow> Actions => Set<ActionRecordRow>();

    public DbSet<ActiveBanRow> ActiveBans => Set<ActiveBanRow>();

    public DbSet<AuditRecordRow> AuditRecords => Set<AuditRecordRow>();

    public DbSet<CorrectionRow> Corrections => Set<CorrectionRow>();

    public DbSet<QueueTelemetryRow> QueueTelemetry => Set<QueueTelemetryRow>();

    public DbSet<InstanceRegistrationRow> InstanceRegistry => Set<InstanceRegistrationRow>();

    public DbSet<SourceOffsetRow> SourceOffsets => Set<SourceOffsetRow>();

    public DbSet<CustomSignatureRow> CustomSignatures => Set<CustomSignatureRow>();

    public DbSet<HostUpgradeCommandRow> HostUpgradeCommands => Set<HostUpgradeCommandRow>();

    public DbSet<IngestionFilterRow> IngestionFilters => Set<IngestionFilterRow>();

    public DbSet<RetentionSettingsRow> RetentionSettings => Set<RetentionSettingsRow>();

    public DbSet<JetPackFeedSettingsRow> JetPackFeedSettings => Set<JetPackFeedSettingsRow>();

    public DbSet<JetPackDesiredAddressRow> JetPackDesiredAddresses => Set<JetPackDesiredAddressRow>();

    public DbSet<LocalModelAdvisorSettingsRow> LocalModelAdvisorSettings => Set<LocalModelAdvisorSettingsRow>();

    public DbSet<LocalModelAdvisorCategoryBandRow> LocalModelAdvisorCategoryBands => Set<LocalModelAdvisorCategoryBandRow>();

    public DbSet<LocalModelAdvisorInjectionPatternRow> LocalModelAdvisorInjectionPatterns => Set<LocalModelAdvisorInjectionPatternRow>();

    public DbSet<LocalModelAdvisorConsultRow> LocalModelAdvisorConsults => Set<LocalModelAdvisorConsultRow>();

    public DbSet<LocalModelAdvisorPromptTemplateRow> LocalModelAdvisorPromptTemplates => Set<LocalModelAdvisorPromptTemplateRow>();

    public DbSet<LocalModelAdvisorResponseCacheRow> LocalModelAdvisorResponseCache => Set<LocalModelAdvisorResponseCacheRow>();

    public DbSet<PolicyThresholdSettingsRow> PolicyThresholdSettings => Set<PolicyThresholdSettingsRow>();

    public DbSet<PolicyPostureSettingsRow> PolicyPostureSettings => Set<PolicyPostureSettingsRow>();

    public DbSet<AdminErrorRow> AdminErrors => Set<AdminErrorRow>();

    public DbSet<MikroTikRouterRow> MikroTikRouters => Set<MikroTikRouterRow>();

    public DbSet<QueueMessageRow> QueueMessages => Set<QueueMessageRow>();

    public DbSet<QueueCounterRow> QueueCounters => Set<QueueCounterRow>();

    public DbSet<AdminUserRow> AdminUsers => Set<AdminUserRow>();

    public DbSet<AdminTotpSecretRow> AdminTotpSecrets => Set<AdminTotpSecretRow>();

    public DbSet<AdminRecoveryCodeRow> AdminRecoveryCodes => Set<AdminRecoveryCodeRow>();

    public DbSet<AdminWebAuthnCredentialRow> AdminWebAuthnCredentials => Set<AdminWebAuthnCredentialRow>();

    public DbSet<AdminUserPreferencesRow> AdminUserPreferences => Set<AdminUserPreferencesRow>();

    public DbSet<AdminSessionRow> AdminSessions => Set<AdminSessionRow>();

    public DbSet<AppPasswordRow> AppPasswords => Set<AppPasswordRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Insert-only reference tables (D-0031): rows are never deleted and
        // natural keys never change; the only permitted mutation is filling
        // a null sources.source_type once a typed writer first observes it.
        modelBuilder.Entity<SourceRow>(entity =>
        {
            entity.ToTable("sources");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.HasIndex(e => e.SourceKey).IsUnique();
        });

        modelBuilder.Entity<ClassifierRow>(entity =>
        {
            entity.ToTable("classifiers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.HasIndex(e => e.ClassifierKey).IsUnique();
        });

        modelBuilder.Entity<PolicyRow>(entity =>
        {
            entity.ToTable("policies");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.HasIndex(e => new { e.PolicyKey, e.PolicyVersion }).IsUnique();
        });

        modelBuilder.Entity<ActionProviderRow>(entity =>
        {
            entity.ToTable("action_providers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.HasIndex(e => e.ProviderKey).IsUnique();
        });

        modelBuilder.Entity<RawObservationRow>(entity =>
        {
            entity.ToTable("raw_observations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ObservedAt);
            entity.HasIndex(e => e.PayloadReference).IsUnique();
            entity.HasIndex(e => e.SourceId);
            entity.HasOne<SourceRow>().WithMany().HasForeignKey(e => e.SourceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NormalizedEventRow>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.RawObservationId);
            entity.HasIndex(e => e.SourceId);
            entity.HasOne<SourceRow>().WithMany().HasForeignKey(e => e.SourceId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.EntitiesJson).HasColumnType("jsonb");
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<IncidentRow>(entity =>
        {
            entity.ToTable("incidents");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.CorrelationKey, e.State });
            entity.HasIndex(e => new { e.WindowStart, e.State });
            entity.HasIndex(e => new { e.State, e.Id }).IsDescending(false, true);
            entity.Property(e => e.EventIdsJson).HasColumnType("jsonb");
            entity.Property(e => e.EvidenceJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ClassificationRow>(entity =>
        {
            entity.ToTable("classifications");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SubjectId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ClassifierId);
            entity.HasOne<ClassifierRow>().WithMany().HasForeignKey(e => e.ClassifierId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.ModelJson).HasColumnType("jsonb");
            entity.Property(e => e.ReasonsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DecisionRow>(entity =>
        {
            entity.ToTable("decisions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClassificationId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.PolicyId);
            entity.HasIndex(e => new { e.Outcome, e.Id }).IsDescending(false, true);
            entity.HasOne<PolicyRow>().WithMany().HasForeignKey(e => e.PolicyId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.GuardrailsJson).HasColumnType("jsonb");
            entity.Property(e => e.ReviewedBy).HasColumnType("text");
            entity.Property(e => e.ReviewedAt).HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<ActionRecordRow>(entity =>
        {
            entity.ToTable("actions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DecisionId);
            entity.HasIndex(e => e.ProviderId);
            entity.HasIndex(e => e.RequestedAt);
            entity.HasOne<ActionProviderRow>().WithMany().HasForeignKey(e => e.ProviderId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.ParametersJson).HasColumnType("jsonb");
            entity.Property(e => e.RollbackJson).HasColumnType("jsonb");
            entity.Property(e => e.ResultsJson).HasColumnType("text");
        });

        modelBuilder.Entity<ActiveBanRow>(entity =>
        {
            entity.ToTable("active_bans");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.Ip).IsUnique();
            entity.Property(e => e.Ip).HasColumnType("text");
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DecisionId).HasColumnType("uuid");
            entity.Property(e => e.ActionId).HasColumnType("uuid");
        });

        modelBuilder.Entity<AuditRecordRow>(entity =>
        {
            entity.ToTable("audit_records");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.SourceId);
            entity.HasIndex(e => new { e.Stage, e.Id }).IsDescending(false, true);
            entity.HasOne<SourceRow>().WithMany().HasForeignKey(e => e.SourceId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.DetailJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CorrectionRow>(entity =>
        {
            entity.ToTable("corrections");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClassificationId);
        });

        modelBuilder.Entity<QueueTelemetryRow>(entity =>
        {
            entity.ToTable("queue_telemetry");
            entity.HasKey(e => new { e.InstanceId, e.QueueName });
        });

        modelBuilder.Entity<InstanceRegistrationRow>(entity =>
        {
            entity.ToTable("instance_registry");
            entity.HasKey(e => e.InstanceId);
            entity.Property(e => e.UpgradeTarget)
                .HasMaxLength(Viegard.Application.Configuration.HostUpgradeCommandPolicy.MaxTargetLength);
        });

        modelBuilder.Entity<SourceOffsetRow>(entity =>
        {
            entity.ToTable("source_offsets");
            entity.HasKey(e => new { e.SourceId, e.Key });
        });

        modelBuilder.Entity<CustomSignatureRow>(entity =>
        {
            entity.ToTable("custom_signatures");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(Viegard.Domain.Configuration.CustomSignature.MaxNameLength);
            entity.Property(e => e.Pattern).HasMaxLength(Viegard.Domain.Configuration.CustomSignature.MaxPatternLength);
            entity.Property(e => e.AdditionalPatternsJson).HasColumnType("text");
            entity.Property(e => e.Category).HasMaxLength(Viegard.Domain.Configuration.CustomSignature.MaxCategoryLength);
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Domain.Configuration.CustomSignature.MaxUpdatedByLength);
        });

        modelBuilder.Entity<HostUpgradeCommandRow>(entity =>
        {
            entity.ToTable("host_upgrade_commands");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => new { e.Target, e.Status, e.RequestedAt });
            entity.HasIndex(e => new { e.Target, e.FinishedAt });
            entity.HasIndex(e => e.Target)
                .IsUnique()
                .HasFilter("status IN (0, 1)");
            entity.Property(e => e.RequestedBy).HasMaxLength(Viegard.Application.Configuration.HostUpgradeCommandPolicy.MaxRequestedByLength);
        });

        modelBuilder.Entity<IngestionFilterRow>(entity =>
        {
            entity.ToTable("ingestion_filters");
            entity.HasKey(e => new { e.SourceType, e.EventKind });
            entity.Property(e => e.SourceType).HasMaxLength(Viegard.Application.Configuration.IngestionFilter.MaxSourceTypeLength);
            entity.Property(e => e.EventKind).HasMaxLength(Viegard.Application.Configuration.IngestionFilter.MaxEventKindLength);
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Application.Configuration.IngestionFilter.MaxUpdatedByLength);
        });

        modelBuilder.Entity<RetentionSettingsRow>(entity =>
        {
            entity.ToTable("retention_settings", table =>
                table.HasCheckConstraint("CK_retention_settings_fixed_id", "id = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Application.Retention.RetentionSettings.MaxUpdatedByLength);
        });

        modelBuilder.Entity<JetPackFeedSettingsRow>(entity =>
        {
            entity.ToTable("jetpack_feed_settings", table =>
                table.HasCheckConstraint("CK_jetpack_feed_settings_fixed_id", "id = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.FeedUrl).HasMaxLength(Viegard.Application.Configuration.JetPackFeedSettings.MaxFeedUrlLength);
            entity.Property(e => e.AddressListName).HasMaxLength(Viegard.Application.Configuration.JetPackFeedSettings.MaxAddressListNameLength);
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Application.Configuration.JetPackFeedSettings.MaxUpdatedByLength);
        });

        modelBuilder.Entity<JetPackDesiredAddressRow>(entity =>
        {
            entity.ToTable("jetpack_desired_addresses");
            entity.HasKey(e => e.Address);
            entity.Property(e => e.Address).HasColumnType("text");
            entity.Property(e => e.FirstSeenAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LastSeenAt).HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<LocalModelAdvisorSettingsRow>(entity =>
        {
            entity.ToTable("local_model_advisor_settings", table =>
                table.HasCheckConstraint("CK_local_model_advisor_settings_fixed_id", "id = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Endpoint).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorSettings.MaxEndpointLength);
            entity.Property(e => e.Model).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorSettings.MaxModelLength);
            entity.Property(e => e.KeepAlive).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorSettings.MaxKeepAliveLength);
            entity.Property(e => e.ResponseCacheTtlHours).HasDefaultValue(72);
            entity.Property(e => e.SecondModelEndpoint)
                .HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorSettings.MaxEndpointLength)
                .HasDefaultValue(Viegard.Application.Configuration.LocalModelAdvisorSettings.DefaultSecondModelEndpoint);
            entity.Property(e => e.SecondModel).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorSettings.MaxModelLength);
            entity.Property(e => e.InjectionAction).HasDefaultValue((int)Viegard.Application.Configuration.AdvisorInjectionAction.SkipAdvisor);
            entity.Property(e => e.MaxDownwardSeverityDelta).HasDefaultValue(1);
            entity.Property(e => e.MaxDownwardConfidenceDelta).HasDefaultValue(0.10);
            entity.Property(e => e.DeEscalationMinModelConfidence).HasDefaultValue(0.70);
            entity.Property(e => e.DeEscalationProtectedSeverity).HasDefaultValue(7);
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorSettings.MaxUpdatedByLength);
        });

        modelBuilder.Entity<LocalModelAdvisorCategoryBandRow>(entity =>
        {
            entity.ToTable("local_model_advisor_category_bands");
            entity.HasKey(e => e.Category);
            entity.Property(e => e.Category).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorCategoryBand.MaxCategoryLength);
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorCategoryBand.MaxUpdatedByLength);
        });

        modelBuilder.Entity<LocalModelAdvisorInjectionPatternRow>(entity =>
        {
            entity.ToTable("local_model_advisor_injection_patterns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.Category).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorInjectionPattern.MaxCategoryLength);
            entity.Property(e => e.Pattern).HasColumnType("text");
            entity.Property(e => e.Description).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorInjectionPattern.MaxDescriptionLength);
            entity.Property(e => e.CreatedBy).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorInjectionPattern.MaxCreatedByLength);
        });


        modelBuilder.Entity<LocalModelAdvisorPromptTemplateRow>(entity =>
        {
            entity.ToTable("local_model_advisor_prompt_templates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => new { e.TemplateId, e.Revision }).IsUnique();
            entity.HasIndex(e => e.TemplateId)
                .IsUnique()
                .HasFilter("is_active");
            entity.Property(e => e.TemplateId).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorPromptTemplateRevision.MaxTemplateIdLength);
            entity.Property(e => e.SystemInstructions).HasColumnType("text");
            entity.Property(e => e.ApplicationInstructions).HasColumnType("text");
            entity.Property(e => e.Note).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorPromptTemplateRevision.MaxNoteLength);
            entity.Property(e => e.CreatedBy).HasMaxLength(Viegard.Application.Configuration.LocalModelAdvisorPromptTemplateRevision.MaxCreatedByLength);
        });

        modelBuilder.Entity<LocalModelAdvisorResponseCacheRow>(entity =>
        {
            entity.ToTable("local_model_advisor_response_cache");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.CacheKey).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.Property(e => e.CacheKey).HasColumnType("text");
            entity.Property(e => e.ModelId).HasColumnType("text");
            entity.Property(e => e.TemplateVersion).HasColumnType("text");
            entity.Property(e => e.ReasonsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<LocalModelAdvisorConsultRow>(entity =>
        {
            entity.ToTable("local_model_advisor_consults");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ClassificationId);
            entity.Property(e => e.Category).HasColumnType("text");
            entity.Property(e => e.FailureKind).HasColumnType("text");
            entity.Property(e => e.ModelId).HasColumnType("text");
            entity.Property(e => e.EnsembleDetailJson).HasColumnType("jsonb");
            entity.Property(e => e.InjectionCategoriesJson)
                .HasColumnType("jsonb")
                .HasDefaultValue("[]");
        });

        modelBuilder.Entity<PolicyThresholdSettingsRow>(entity =>
        {
            entity.ToTable("policy_threshold_settings", table =>
                table.HasCheckConstraint("CK_policy_threshold_settings_fixed_id", "id = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Application.Policy.PolicyThresholdSettings.MaxUpdatedByLength);
        });

        modelBuilder.Entity<PolicyPostureSettingsRow>(entity =>
        {
            entity.ToTable("policy_posture_settings", table =>
                table.HasCheckConstraint("CK_policy_posture_settings_fixed_id", "id = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.UpdatedBy).HasMaxLength(Viegard.Application.Policy.PolicyPostureSettings.MaxUpdatedByLength);
        });

        modelBuilder.Entity<AdminErrorRow>(entity =>
        {
            entity.ToTable("admin_errors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.OccurredAt).IsDescending();
            entity.Property(e => e.RequestId).HasMaxLength(Viegard.Domain.Admin.AdminError.MaxRequestIdLength);
            entity.Property(e => e.Path).HasMaxLength(Viegard.Domain.Admin.AdminError.MaxPathLength);
            entity.Property(e => e.Method).HasMaxLength(Viegard.Domain.Admin.AdminError.MaxMethodLength);
            entity.Property(e => e.Username).HasMaxLength(Viegard.Domain.Admin.AdminError.MaxUsernameLength);
            entity.Property(e => e.ExceptionType).HasMaxLength(Viegard.Domain.Admin.AdminError.MaxExceptionTypeLength);
            entity.Property(e => e.Message).HasMaxLength(Viegard.Domain.Admin.AdminError.MaxMessageLength);
            entity.Property(e => e.StackTrace).HasMaxLength(Viegard.Domain.Admin.AdminError.MaxStackTraceLength);
        });

        modelBuilder.Entity<MikroTikRouterRow>(entity =>
        {
            entity.ToTable("mikrotik_routers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasColumnType("text");
            entity.Property(e => e.BaseUrl).HasColumnType("text");
            entity.Property(e => e.TransportMode).HasColumnType("text");
            entity.Property(e => e.PinnedCertificateSha256).HasColumnName("pinned_cert_sha256").HasColumnType("text");
            entity.Property(e => e.Username).HasColumnType("text");
            entity.Property(e => e.PasswordCiphertext).HasColumnType("text");
            entity.Property(e => e.UpdatedBy).HasColumnType("text");
        });

        modelBuilder.Entity<QueueMessageRow>(entity =>
        {
            entity.ToTable("queue_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.HasIndex(e => new { e.QueueName, e.DeadLettered, e.LeasedUntil, e.Id });
            entity.HasIndex(e => new { e.DeadLettered, e.EnqueuedAt });
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<QueueCounterRow>(entity =>
        {
            entity.ToTable("queue_counters");
            entity.HasKey(e => e.QueueName);
        });

        modelBuilder.Entity<AdminUserRow>(entity =>
        {
            entity.ToTable("admin_users");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<AdminTotpSecretRow>(entity =>
        {
            entity.ToTable("admin_totp_secrets");
            entity.HasKey(e => e.UserId);
            entity.HasOne<AdminUserRow>().WithOne().HasForeignKey<AdminTotpSecretRow>(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminRecoveryCodeRow>(entity =>
        {
            entity.ToTable("admin_recovery_codes");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasOne<AdminUserRow>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminWebAuthnCredentialRow>(entity =>
        {
            entity.ToTable("admin_webauthn_credentials");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CredentialId).IsUnique();
            entity.HasOne<AdminUserRow>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.CredentialId).HasColumnType("bytea");
            entity.Property(e => e.PublicKey).HasColumnType("bytea");
        });

        modelBuilder.Entity<AdminUserPreferencesRow>(entity =>
        {
            entity.ToTable("admin_user_preferences");
            entity.HasKey(e => e.UserId);
            entity.HasOne<AdminUserRow>().WithOne().HasForeignKey<AdminUserPreferencesRow>(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminSessionRow>(entity =>
        {
            entity.ToTable("admin_sessions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.AbsoluteExpiresAt);
            entity.HasIndex(e => e.IdleExpiresAt);
            entity.HasIndex(e => e.RevokedAt);
            entity.HasOne<AdminUserRow>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppPasswordRow>(entity =>
        {
            entity.ToTable("app_passwords");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.LookupKey).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(128);
            entity.Property(e => e.LookupKey).HasMaxLength(16);
            entity.Property(e => e.SecretHash).HasMaxLength(64);
            entity.HasOne<AdminUserRow>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // snake_case column names everywhere: PostgreSQL convention, and the
        // durable queue's raw SQL depends on it.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }

        modelBuilder.Entity<MikroTikRouterRow>()
            .Property(e => e.PinnedCertificateSha256)
            .HasColumnName("pinned_cert_sha256");

        modelBuilder.Entity<LocalModelAdvisorConsultRow>()
            .Property(e => e.EnsembleDetailJson)
            .HasColumnName("ensemble_detail");

        modelBuilder.Entity<LocalModelAdvisorConsultRow>()
            .Property(e => e.InjectionCategoriesJson)
            .HasColumnName("injection_categories");
    }

    private static string ToSnakeCase(string name) =>
        SnakeCasePattern().Replace(name, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex SnakeCasePattern();
}
