using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveBans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "active_bans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ip = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_active_bans", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_active_bans_ip",
                table: "active_bans",
                column: "ip",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "active_bans");
        }
    }
}
