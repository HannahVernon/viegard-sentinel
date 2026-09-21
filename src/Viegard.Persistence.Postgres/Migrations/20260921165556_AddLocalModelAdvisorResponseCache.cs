using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelAdvisorResponseCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "response_cache_enabled",
                table: "local_model_advisor_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "response_cache_ttl_hours",
                table: "local_model_advisor_settings",
                type: "integer",
                nullable: false,
                defaultValue: 72);

            migrationBuilder.AddColumn<bool>(
                name: "served_from_cache",
                table: "local_model_advisor_consults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "local_model_advisor_response_cache",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cache_key = table.Column<string>(type: "text", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: false),
                    template_version = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reasons_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_model_advisor_response_cache", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_model_advisor_response_cache_cache_key",
                table: "local_model_advisor_response_cache",
                column: "cache_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_model_advisor_response_cache_expires_at",
                table: "local_model_advisor_response_cache",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_model_advisor_response_cache");

            migrationBuilder.DropColumn(
                name: "response_cache_enabled",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "response_cache_ttl_hours",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "served_from_cache",
                table: "local_model_advisor_consults");
        }
    }
}
