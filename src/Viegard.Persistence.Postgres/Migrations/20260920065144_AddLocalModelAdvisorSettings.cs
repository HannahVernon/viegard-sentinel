using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelAdvisorSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_model_advisor_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    temperature = table.Column<double>(type: "double precision", nullable: false),
                    timeout_ms = table.Column<int>(type: "integer", nullable: false),
                    keep_alive = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invoke_confidence_min = table.Column<double>(type: "double precision", nullable: false),
                    invoke_confidence_max = table.Column<double>(type: "double precision", nullable: false),
                    max_severity_delta = table.Column<int>(type: "integer", nullable: false),
                    max_confidence_delta = table.Column<double>(type: "double precision", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    seeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_model_advisor_settings", x => x.id);
                    table.CheckConstraint("CK_local_model_advisor_settings_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_model_advisor_settings");
        }
    }
}
