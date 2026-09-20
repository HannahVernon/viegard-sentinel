using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelAdvisorConsults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "local_model_advisor_consults_days",
                table: "retention_settings",
                type: "integer",
                nullable: true,
                defaultValue: 90);

            migrationBuilder.CreateTable(
                name: "local_model_advisor_consults",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    classification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    base_severity = table.Column<int>(type: "integer", nullable: false),
                    final_severity = table.Column<int>(type: "integer", nullable: false),
                    base_confidence = table.Column<double>(type: "double precision", nullable: false),
                    final_confidence = table.Column<double>(type: "double precision", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    failure_kind = table.Column<string>(type: "text", nullable: true),
                    model_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_model_advisor_consults", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_model_advisor_consults_classification_id",
                table: "local_model_advisor_consults",
                column: "classification_id");

            migrationBuilder.CreateIndex(
                name: "IX_local_model_advisor_consults_created_at",
                table: "local_model_advisor_consults",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_model_advisor_consults");

            migrationBuilder.DropColumn(
                name: "local_model_advisor_consults_days",
                table: "retention_settings");
        }
    }
}
