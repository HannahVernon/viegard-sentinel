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

    public DbSet<NormalizedEventRow> Events => Set<NormalizedEventRow>();

    public DbSet<IncidentRow> Incidents => Set<IncidentRow>();

    public DbSet<ClassificationRow> Classifications => Set<ClassificationRow>();

    public DbSet<DecisionRow> Decisions => Set<DecisionRow>();

    public DbSet<ActionRecordRow> Actions => Set<ActionRecordRow>();

    public DbSet<AuditRecordRow> AuditRecords => Set<AuditRecordRow>();

    public DbSet<CorrectionRow> Corrections => Set<CorrectionRow>();

    public DbSet<QueueTelemetryRow> QueueTelemetry => Set<QueueTelemetryRow>();

    public DbSet<SourceOffsetRow> SourceOffsets => Set<SourceOffsetRow>();

    public DbSet<QueueMessageRow> QueueMessages => Set<QueueMessageRow>();

    public DbSet<QueueCounterRow> QueueCounters => Set<QueueCounterRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RawObservationRow>(entity =>
        {
            entity.ToTable("raw_observations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PayloadReference).IsUnique();
            entity.HasIndex(e => e.SourceId);
        });

        modelBuilder.Entity<NormalizedEventRow>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.RawObservationId);
            entity.Property(e => e.EntitiesJson).HasColumnType("jsonb");
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<IncidentRow>(entity =>
        {
            entity.ToTable("incidents");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.CorrelationKey, e.State });
            entity.Property(e => e.EventIdsJson).HasColumnType("jsonb");
            entity.Property(e => e.EvidenceJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ClassificationRow>(entity =>
        {
            entity.ToTable("classifications");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SubjectId);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.ModelJson).HasColumnType("jsonb");
            entity.Property(e => e.ReasonsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DecisionRow>(entity =>
        {
            entity.ToTable("decisions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClassificationId);
            entity.Property(e => e.GuardrailsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ActionRecordRow>(entity =>
        {
            entity.ToTable("actions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DecisionId);
            entity.Property(e => e.ParametersJson).HasColumnType("jsonb");
            entity.Property(e => e.RollbackJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AuditRecordRow>(entity =>
        {
            entity.ToTable("audit_records");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Timestamp);
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

        modelBuilder.Entity<SourceOffsetRow>(entity =>
        {
            entity.ToTable("source_offsets");
            entity.HasKey(e => new { e.SourceId, e.Key });
        });

        modelBuilder.Entity<QueueMessageRow>(entity =>
        {
            entity.ToTable("queue_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.HasIndex(e => new { e.QueueName, e.DeadLettered, e.LeasedUntil, e.Id });
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<QueueCounterRow>(entity =>
        {
            entity.ToTable("queue_counters");
            entity.HasKey(e => e.QueueName);
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
    }

    private static string ToSnakeCase(string name) =>
        SnakeCasePattern().Replace(name, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex SnakeCasePattern();
}
