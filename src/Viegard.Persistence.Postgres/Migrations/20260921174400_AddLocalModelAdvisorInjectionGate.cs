using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelAdvisorInjectionGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "injection_action",
                table: "local_model_advisor_settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "advisor_skipped_for_injection",
                table: "local_model_advisor_consults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "injection_categories",
                table: "local_model_advisor_consults",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "injection_detected",
                table: "local_model_advisor_consults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "local_model_advisor_injection_patterns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pattern = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_model_advisor_injection_patterns", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_model_advisor_injection_patterns_created_at",
                table: "local_model_advisor_injection_patterns",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_model_advisor_injection_patterns");

            migrationBuilder.DropColumn(
                name: "injection_action",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "advisor_skipped_for_injection",
                table: "local_model_advisor_consults");

            migrationBuilder.DropColumn(
                name: "injection_categories",
                table: "local_model_advisor_consults");

            migrationBuilder.DropColumn(
                name: "injection_detected",
                table: "local_model_advisor_consults");
        }
    }
}
