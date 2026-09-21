using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelAdvisorCategoryBands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_model_advisor_category_bands",
                columns: table => new
                {
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: true),
                    invoke_confidence_min = table.Column<double>(type: "double precision", nullable: true),
                    invoke_confidence_max = table.Column<double>(type: "double precision", nullable: true),
                    max_severity_delta = table.Column<int>(type: "integer", nullable: true),
                    max_confidence_delta = table.Column<double>(type: "double precision", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_model_advisor_category_bands", x => x.category);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_model_advisor_category_bands");
        }
    }
}
