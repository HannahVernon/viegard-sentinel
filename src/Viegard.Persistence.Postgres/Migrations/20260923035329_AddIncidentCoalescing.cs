using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentCoalescing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "coalesce_until",
                table: "incidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "decided_event_count",
                table: "incidents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "superseded_at",
                table: "decisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "superseded_by_decision_id",
                table: "decisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "incident_coalescing_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    settle_window_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_coalesce_window_seconds = table.Column<int>(type: "integer", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_coalescing_settings", x => x.id);
                    table.CheckConstraint("CK_incident_coalescing_settings_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_coalescing_settings");

            migrationBuilder.DropColumn(
                name: "coalesce_until",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "decided_event_count",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "superseded_by_decision_id",
                table: "decisions");
        }
    }
}
