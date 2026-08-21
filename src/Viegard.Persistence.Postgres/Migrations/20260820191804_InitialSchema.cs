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
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    rollback_json = table.Column<string>(type: "jsonb", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stage = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    classification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "classifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_kind = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    classifier_id = table.Column<string>(type: "text", nullable: false),
                    model_json = table.Column<string>(type: "jsonb", nullable: true),
                    category = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    reasons_json = table.Column<string>(type: "jsonb", nullable: false),
                    recommended_action = table.Column<string>(type: "text", nullable: true),
                    uncertainty = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "corrections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    classification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    corrected_category = table.Column<string>(type: "text", nullable: false),
                    corrected_by = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corrections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    classification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<string>(type: "text", nullable: false),
                    policy_version = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    rationale = table.Column<string>(type: "text", nullable: false),
                    guardrails_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    entities_json = table.Column<string>(type: "jsonb", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    raw_observation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_key = table.Column<string>(type: "text", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queue_counters",
                columns: table => new
                {
                    queue_name = table.Column<string>(type: "text", nullable: false),
                    enqueued = table.Column<long>(type: "bigint", nullable: false),
                    completed = table.Column<long>(type: "bigint", nullable: false),
                    abandoned = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_counters", x => x.queue_name);
                });

            migrationBuilder.CreateTable(
                name: "queue_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    queue_name = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    delivery_count = table.Column<int>(type: "integer", nullable: false),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    leased_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queue_telemetry",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    queue_name = table.Column<string>(type: "text", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    in_flight = table.Column<int>(type: "integer", nullable: false),
                    oldest_pending_enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    total_enqueued = table.Column<long>(type: "bigint", nullable: false),
                    total_completed = table.Column<long>(type: "bigint", nullable: false),
                    total_abandoned = table.Column<long>(type: "bigint", nullable: false),
                    dead_letter_count = table.Column<int>(type: "integer", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_telemetry", x => new { x.instance_id, x.queue_name });
                });

            migrationBuilder.CreateTable(
                name: "raw_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_reference = table.Column<string>(type: "text", nullable: false),
                    ingest_offset = table.Column<string>(type: "text", nullable: true),
                    raw_payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_observations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source_offsets",
                columns: table => new
                {
                    source_id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_offsets", x => new { x.source_id, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_actions_decision_id",
                table: "actions",
                column: "decision_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_timestamp",
                table: "audit_records",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_classifications_created_at",
                table: "classifications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_classifications_subject_id",
                table: "classifications",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_corrections_classification_id",
                table: "corrections",
                column: "classification_id");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_classification_id",
                table: "decisions",
                column: "classification_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_occurred_at",
                table: "events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_events_raw_observation_id",
                table: "events",
                column: "raw_observation_id");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_correlation_key_state",
                table: "incidents",
                columns: new[] { "correlation_key", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_queue_messages_queue_name_dead_lettered_leased_until_id",
                table: "queue_messages",
                columns: new[] { "queue_name", "dead_lettered", "leased_until", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_raw_observations_payload_reference",
                table: "raw_observations",
                column: "payload_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_raw_observations_source_id",
                table: "raw_observations",
                column: "source_id");
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
