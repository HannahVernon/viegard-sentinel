using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDohBlocklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "doh_blocklist_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    primary_feed_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    secondary_feed_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    address_list_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    fetch_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    probe_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    probe_canary_fqdn = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    probe_expected_token = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    probe_endpoint_path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    probe_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    probe_concurrency = table.Column<int>(type: "integer", nullable: false),
                    probe_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    apply_to_routers = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doh_blocklist_settings", x => x.id);
                    table.CheckConstraint("CK_doh_blocklist_settings_fixed_id", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "doh_desired_addresses",
                columns: table => new
                {
                    address = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doh_desired_addresses", x => x.address);
                });

            migrationBuilder.CreateTable(
                name: "doh_probe_results",
                columns: table => new
                {
                    address = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    token_matched = table.Column<bool>(type: "boolean", nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_probed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doh_probe_results", x => x.address);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "doh_blocklist_settings");

            migrationBuilder.DropTable(
                name: "doh_desired_addresses");

            migrationBuilder.DropTable(
                name: "doh_probe_results");
        }
    }
}
