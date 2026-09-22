using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddClassifierSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "classifier_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    score_for_full_confidence = table.Column<double>(type: "double precision", nullable: false),
                    severity_per_score_point = table.Column<double>(type: "double precision", nullable: false),
                    block_recommendation_score = table.Column<double>(type: "double precision", nullable: false),
                    repeat_confidence_min_events = table.Column<int>(type: "integer", nullable: false),
                    repeat_confidence_coefficient = table.Column<double>(type: "double precision", nullable: false),
                    repeat_confidence_bonus_cap = table.Column<double>(type: "double precision", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classifier_settings", x => x.id);
                    table.CheckConstraint("CK_classifier_settings_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "classifier_settings");
        }
    }
}
