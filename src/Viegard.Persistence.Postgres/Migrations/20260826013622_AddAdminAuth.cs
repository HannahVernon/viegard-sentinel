using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    password_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    totp_enrolled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "admin_recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "FK_admin_recovery_codes_admin_users_user_id",
                        column: x => x.user_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "admin_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip = table.Column<string>(type: "text", nullable: false),
                    ip_binding_mode = table.Column<string>(type: "text", nullable: false),
                    user_agent = table.Column<string>(type: "text", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    step_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_admin_sessions_admin_users_user_id",
                        column: x => x.user_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "admin_totp_secrets",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_base32 = table.Column<string>(type: "text", nullable: false),
                    last_accepted_step = table.Column<long>(type: "bigint", nullable: true),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_totp_secrets", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_admin_totp_secrets_admin_users_user_id",
                        column: x => x.user_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_recovery_codes_user_id",
                table: "admin_recovery_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_absolute_expires_at",
                table: "admin_sessions",
                column: "absolute_expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_idle_expires_at",
                table: "admin_sessions",
                column: "idle_expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_user_id",
                table: "admin_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_admin_users_username",
                table: "admin_users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_recovery_codes");

            migrationBuilder.DropTable(
                name: "admin_sessions");

            migrationBuilder.DropTable(
                name: "admin_totp_secrets");

            migrationBuilder.DropTable(
                name: "admin_users");
        }
    }
}
