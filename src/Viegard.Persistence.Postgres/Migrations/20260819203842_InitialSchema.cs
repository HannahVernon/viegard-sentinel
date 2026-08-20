using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "text", nullable: false),
                    OperationId = table.Column<string>(type: "text", nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RollbackJson = table.Column<string>(type: "jsonb", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<string>(type: "text", nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClassificationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DetailJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "classifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectKind = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClassifierId = table.Column<string>(type: "text", nullable: false),
                    ModelJson = table.Column<string>(type: "jsonb", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    ReasonsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecommendedAction = table.Column<string>(type: "text", nullable: true),
                    Uncertainty = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "corrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClassificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrectedCategory = table.Column<string>(type: "text", nullable: false),
                    CorrectedBy = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corrections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClassificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<string>(type: "text", nullable: false),
                    PolicyVersion = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: false),
                    GuardrailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<string>(type: "text", nullable: false),
                    SourceType = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EntitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    RawObservationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationKey = table.Column<string>(type: "text", nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "queue_counters",
                columns: table => new
                {
                    QueueName = table.Column<string>(type: "text", nullable: false),
                    Enqueued = table.Column<long>(type: "bigint", nullable: false),
                    Completed = table.Column<long>(type: "bigint", nullable: false),
                    Abandoned = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_counters", x => x.QueueName);
                });

            migrationBuilder.CreateTable(
                name: "queue_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    QueueName = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    DeliveryCount = table.Column<int>(type: "integer", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeasedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadLettered = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "queue_telemetry",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "text", nullable: false),
                    QueueName = table.Column<string>(type: "text", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    InFlight = table.Column<int>(type: "integer", nullable: false),
                    OldestPendingEnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalEnqueued = table.Column<long>(type: "bigint", nullable: false),
                    TotalCompleted = table.Column<long>(type: "bigint", nullable: false),
                    TotalAbandoned = table.Column<long>(type: "bigint", nullable: false),
                    DeadLetterCount = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_telemetry", x => new { x.InstanceId, x.QueueName });
                });

            migrationBuilder.CreateTable(
                name: "raw_observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<string>(type: "text", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadReference = table.Column<string>(type: "text", nullable: false),
                    IngestOffset = table.Column<string>(type: "text", nullable: true),
                    RawPayload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_observations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "source_offsets",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_offsets", x => new { x.SourceId, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_actions_DecisionId",
                table: "actions",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_Timestamp",
                table: "audit_records",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_classifications_CreatedAt",
                table: "classifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_classifications_SubjectId",
                table: "classifications",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_corrections_ClassificationId",
                table: "corrections",
                column: "ClassificationId");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_ClassificationId",
                table: "decisions",
                column: "ClassificationId");

            migrationBuilder.CreateIndex(
                name: "IX_events_OccurredAt",
                table: "events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_events_RawObservationId",
                table: "events",
                column: "RawObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_CorrelationKey_State",
                table: "incidents",
                columns: new[] { "CorrelationKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_queue_messages_QueueName_DeadLettered_LeasedUntil_Id",
                table: "queue_messages",
                columns: new[] { "QueueName", "DeadLettered", "LeasedUntil", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_raw_observations_PayloadReference",
                table: "raw_observations",
                column: "PayloadReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_raw_observations_SourceId",
                table: "raw_observations",
                column: "SourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actions");

            migrationBuilder.DropTable(
                name: "audit_records");

            migrationBuilder.DropTable(
                name: "classifications");

            migrationBuilder.DropTable(
                name: "corrections");

            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "queue_counters");

            migrationBuilder.DropTable(
                name: "queue_messages");

            migrationBuilder.DropTable(
                name: "queue_telemetry");

            migrationBuilder.DropTable(
                name: "raw_observations");

            migrationBuilder.DropTable(
                name: "source_offsets");
        }
    }
}
