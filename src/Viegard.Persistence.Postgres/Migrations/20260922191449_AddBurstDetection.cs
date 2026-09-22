using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBurstDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "burst_cooldowns",
                columns: table => new
                {
                    signal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_burst_cooldowns", x => new { x.signal_id, x.source_key });
                });

            migrationBuilder.CreateTable(
                name: "burst_detection_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    global_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    auth_failure_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    auth_failure_threshold = table.Column<int>(type: "integer", nullable: false),
                    auth_failure_window_seconds = table.Column<int>(type: "integer", nullable: false),
                    auth_failure_cooldown_seconds = table.Column<int>(type: "integer", nullable: false),
                    auth_failure_action_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_burst_detection_settings", x => x.id);
                    table.CheckConstraint("CK_burst_detection_settings_fixed_id", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "burst_windows",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    signal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_burst_windows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_burst_windows_signal_id_source_key_occurred_at",
                table: "burst_windows",
                columns: new[] { "signal_id", "source_key", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "burst_cooldowns");

            migrationBuilder.DropTable(
                name: "burst_detection_settings");

            migrationBuilder.DropTable(
                name: "burst_windows");
        }
    }
}
