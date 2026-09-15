using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "retention_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    raw_observations_days = table.Column<int>(type: "integer", nullable: true),
                    events_days = table.Column<int>(type: "integer", nullable: true),
                    incidents_days = table.Column<int>(type: "integer", nullable: true),
                    classifications_days = table.Column<int>(type: "integer", nullable: true),
                    decisions_days = table.Column<int>(type: "integer", nullable: true),
                    actions_days = table.Column<int>(type: "integer", nullable: true),
                    audit_records_days = table.Column<int>(type: "integer", nullable: true),
                    dead_lettered_queue_messages_days = table.Column<int>(type: "integer", nullable: true),
                    expired_admin_sessions_days = table.Column<int>(type: "integer", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    seeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    last_cycle_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_cycle_counts_json = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_settings", x => x.id);
                    table.CheckConstraint("CK_retention_settings_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retention_settings");
        }
    }
}
