using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddListFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These index builds run once during startup migrations before
            // workers begin processing.  On large live tables, especially
            // audit_records, that can pause the stack for a bounded minute or
            // two while PostgreSQL builds the access paths.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.AddColumn<string>(
                name: "upgrade_target",
                table: "instance_registry",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_state_id",
                table: "incidents",
                columns: new[] { "state", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_decisions_outcome_id",
                table: "decisions",
                columns: new[] { "outcome", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_stage_id",
                table: "audit_records",
                columns: new[] { "stage", "id" },
                descending: new[] { false, true });

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_events_payload_json_trgm"
                ON events USING gin ((payload_json::text) gin_trgm_ops);

                CREATE INDEX IF NOT EXISTS "IX_incidents_correlation_key_trgm"
                ON incidents USING gin (correlation_key gin_trgm_ops);

                CREATE INDEX IF NOT EXISTS "IX_decisions_rationale_trgm"
                ON decisions USING gin (rationale gin_trgm_ops);

                CREATE INDEX IF NOT EXISTS "IX_audit_records_summary_trgm"
                ON audit_records USING gin (summary gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_audit_records_summary_trgm";
                DROP INDEX IF EXISTS "IX_decisions_rationale_trgm";
                DROP INDEX IF EXISTS "IX_incidents_correlation_key_trgm";
                DROP INDEX IF EXISTS "IX_events_payload_json_trgm";
                """);

            migrationBuilder.DropIndex(
                name: "IX_incidents_state_id",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "IX_decisions_outcome_id",
                table: "decisions");

            migrationBuilder.DropIndex(
                name: "IX_audit_records_stage_id",
                table: "audit_records");

            migrationBuilder.DropColumn(
                name: "upgrade_target",
                table: "instance_registry");
        }
    }
}
