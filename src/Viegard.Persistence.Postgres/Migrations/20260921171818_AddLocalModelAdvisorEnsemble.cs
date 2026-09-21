using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelAdvisorEnsemble : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ensemble_enabled",
                table: "local_model_advisor_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "second_model",
                table: "local_model_advisor_settings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "second_model_endpoint",
                table: "local_model_advisor_settings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "http://127.0.0.1:11434");

            migrationBuilder.AddColumn<string>(
                name: "ensemble_detail",
                table: "local_model_advisor_consults",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ensemble_enabled",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "second_model",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "second_model_endpoint",
                table: "local_model_advisor_settings");

            migrationBuilder.DropColumn(
                name: "ensemble_detail",
                table: "local_model_advisor_consults");
        }
    }
}
