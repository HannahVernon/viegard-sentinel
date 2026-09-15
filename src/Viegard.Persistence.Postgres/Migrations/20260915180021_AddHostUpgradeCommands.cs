using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddHostUpgradeCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "host_upgrade_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_upgrade_commands", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_host_upgrade_commands_target",
                table: "host_upgrade_commands",
                column: "target",
                unique: true,
                filter: "status IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_host_upgrade_commands_target_finished_at",
                table: "host_upgrade_commands",
                columns: new[] { "target", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "IX_host_upgrade_commands_target_status_requested_at",
                table: "host_upgrade_commands",
                columns: new[] { "target", "status", "requested_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_upgrade_commands");
        }
    }
}
