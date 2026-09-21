using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvisorDeEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "de_escalation_enabled",
                table: "local_model_advisor_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "de_escalation_min_model_confidence",
                table: "local_model_advisor_settings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.69999999999999996);

            migrationBuilder.AddColumn<int>(
                name: "de_escalation_protected_severity",
                table: "local_model_advisor_settings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<double>(
                name: "max_downward_confidence_delta",
                table: "local_model_advisor_settings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.10000000000000001);

            migrationBuilder.AddColumn<int>(
                name: "max_downward_severity_delta",
                table: "local_model_advisor_settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "de_escalation_enabled",
                table: "local_model_advisor_category_bands",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "max_downward_confidence_delta",
                table: "local_model_advisor_category_bands",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_downward_severity_delta",
                table: "local_model_advisor_category_bands",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "de_escalation_enabled",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "de_escalation_min_model_confidence",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "de_escalation_protected_severity",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "max_downward_confidence_delta",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "max_downward_severity_delta",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "de_escalation_enabled",
                table: "local_model_advisor_category_bands");

            migrationBuilder.DropColumn(
                name: "max_downward_confidence_delta",
                table: "local_model_advisor_category_bands");

            migrationBuilder.DropColumn(
                name: "max_downward_severity_delta",
                table: "local_model_advisor_category_bands");
        }
    }
}
