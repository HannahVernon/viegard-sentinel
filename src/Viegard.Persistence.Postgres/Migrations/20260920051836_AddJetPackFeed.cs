using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddJetPackFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jetpack_desired_addresses",
                columns: table => new
                {
                    address = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jetpack_desired_addresses", x => x.address);
                });

            migrationBuilder.CreateTable(
                name: "jetpack_feed_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    feed_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    fetch_interval = table.Column<TimeSpan>(type: "interval", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    address_list_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    seeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jetpack_feed_settings", x => x.id);
                    table.CheckConstraint("CK_jetpack_feed_settings_fixed_id", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jetpack_desired_addresses");

            migrationBuilder.DropTable(
                name: "jetpack_feed_settings");
        }
    }
}
