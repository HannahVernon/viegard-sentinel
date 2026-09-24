using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDohReconciliationProposal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "doh_reconciliation_proposal",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    detail_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doh_reconciliation_proposal", x => x.id);
                    table.CheckConstraint("CK_doh_reconciliation_proposal_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "doh_reconciliation_proposal");
        }
    }
}
