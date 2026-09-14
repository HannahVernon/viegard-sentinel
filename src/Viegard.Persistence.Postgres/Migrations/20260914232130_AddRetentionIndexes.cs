using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_raw_observations_observed_at",
                table: "raw_observations",
                column: "observed_at");

            migrationBuilder.CreateIndex(
                name: "IX_queue_messages_dead_lettered_enqueued_at",
                table: "queue_messages",
                columns: new[] { "dead_lettered", "enqueued_at" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_window_start_state",
                table: "incidents",
                columns: new[] { "window_start", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_decisions_created_at",
                table: "decisions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_revoked_at",
                table: "admin_sessions",
                column: "revoked_at");

            migrationBuilder.CreateIndex(
                name: "IX_actions_requested_at",
                table: "actions",
                column: "requested_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_raw_observations_observed_at",
                table: "raw_observations");

            migrationBuilder.DropIndex(
                name: "IX_queue_messages_dead_lettered_enqueued_at",
                table: "queue_messages");

            migrationBuilder.DropIndex(
                name: "IX_incidents_window_start_state",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "IX_decisions_created_at",
                table: "decisions");

            migrationBuilder.DropIndex(
                name: "IX_admin_sessions_revoked_at",
                table: "admin_sessions");

            migrationBuilder.DropIndex(
                name: "IX_actions_requested_at",
                table: "actions");
        }
    }
}
