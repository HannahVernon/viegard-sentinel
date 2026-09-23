using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSecuritySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_security_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    step_up_validity_seconds = table.Column<int>(type: "integer", nullable: false),
                    resume_stash_ttl_seconds = table.Column<int>(type: "integer", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_security_settings", x => x.id);
                    table.CheckConstraint("CK_session_security_settings_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_security_settings");
        }
    }
}
