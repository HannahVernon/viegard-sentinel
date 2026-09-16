using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyThresholdSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "policy_threshold_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    review_confidence = table.Column<double>(type: "double precision", nullable: false),
                    action_confidence = table.Column<double>(type: "double precision", nullable: false),
                    action_min_severity = table.Column<int>(type: "integer", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_threshold_settings", x => x.id);
                    table.CheckConstraint("CK_policy_threshold_settings_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "policy_threshold_settings");
        }
    }
}
