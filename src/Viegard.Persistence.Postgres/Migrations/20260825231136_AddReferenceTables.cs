using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Viegard.Persistence.Postgres.Migrations
{
    /// <summary>
    /// D-0031: normalizes low-cardinality descriptor strings (source,
    /// classifier, policy, action provider) into insert-only reference
    /// tables and swaps the fact tables' text columns for int foreign keys.
    /// The scaffolded text-to-integer AlterColumn operations are replaced
    /// with explicit add/backfill/drop/rename SQL because PostgreSQL has no
    /// implicit text-to-integer cast.  All SQL is unqualified and follows
    /// the connection search path (schema-agnostic, like the model).
    /// </summary>
    public partial class AddReferenceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    source_type = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "classifiers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    classifier_key = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classifiers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "policies",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    policy_key = table.Column<string>(type: "text", nullable: false),
                    policy_version = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "action_providers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_providers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sources_source_key",
                table: "sources",
                column: "source_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_classifiers_classifier_key",
                table: "classifiers",
                column: "classifier_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policies_policy_key_policy_version",
                table: "policies",
                columns: new[] { "policy_key", "policy_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_action_providers_provider_key",
                table: "action_providers",
                column: "provider_key",
                unique: true);

            // Backfill the reference tables from every distinct descriptor
            // already present.  Events supply source types; keys seen only
            // in raw observations or audit records start untyped and are
            // filled once a typed writer next resolves them.
            migrationBuilder.Sql("""
                INSERT INTO sources (source_key, source_type, first_seen_at)
                SELECT DISTINCT ON (e.source_id) e.source_id, e.source_type, now()
                FROM events e
                ORDER BY e.source_id;

                INSERT INTO sources (source_key, source_type, first_seen_at)
                SELECT DISTINCT r.source_id, NULL, now()
                FROM raw_observations r
                ON CONFLICT (source_key) DO NOTHING;

                INSERT INTO sources (source_key, source_type, first_seen_at)
                SELECT DISTINCT a.source_id, NULL, now()
                FROM audit_records a
                WHERE a.source_id IS NOT NULL
                ON CONFLICT (source_key) DO NOTHING;

                INSERT INTO classifiers (classifier_key, first_seen_at)
                SELECT DISTINCT c.classifier_id, now()
                FROM classifications c;

                INSERT INTO policies (policy_key, policy_version, first_seen_at)
                SELECT DISTINCT d.policy_id, d.policy_version, now()
                FROM decisions d;

                INSERT INTO action_providers (provider_key, first_seen_at)
                SELECT DISTINCT a.provider_id, now()
                FROM actions a;
                """);

            // Swap each fact table's text descriptor for the reference id.
            migrationBuilder.Sql("""
                ALTER TABLE raw_observations ADD COLUMN source_ref integer;
                UPDATE raw_observations r SET source_ref = s.id FROM sources s WHERE s.source_key = r.source_id;
                ALTER TABLE raw_observations DROP COLUMN source_id;
                ALTER TABLE raw_observations RENAME COLUMN source_ref TO source_id;
                ALTER TABLE raw_observations ALTER COLUMN source_id SET NOT NULL;

                ALTER TABLE events ADD COLUMN source_ref integer;
                UPDATE events e SET source_ref = s.id FROM sources s WHERE s.source_key = e.source_id;
                ALTER TABLE events DROP COLUMN source_id;
                ALTER TABLE events DROP COLUMN source_type;
                ALTER TABLE events RENAME COLUMN source_ref TO source_id;
                ALTER TABLE events ALTER COLUMN source_id SET NOT NULL;

                ALTER TABLE audit_records ADD COLUMN source_ref integer;
                UPDATE audit_records a SET source_ref = s.id FROM sources s WHERE s.source_key = a.source_id;
                ALTER TABLE audit_records DROP COLUMN source_id;
                ALTER TABLE audit_records RENAME COLUMN source_ref TO source_id;

                ALTER TABLE classifications ADD COLUMN classifier_ref integer;
                UPDATE classifications c SET classifier_ref = k.id FROM classifiers k WHERE k.classifier_key = c.classifier_id;
                ALTER TABLE classifications DROP COLUMN classifier_id;
                ALTER TABLE classifications RENAME COLUMN classifier_ref TO classifier_id;
                ALTER TABLE classifications ALTER COLUMN classifier_id SET NOT NULL;

                ALTER TABLE decisions ADD COLUMN policy_ref integer;
                UPDATE decisions d SET policy_ref = p.id FROM policies p WHERE p.policy_key = d.policy_id AND p.policy_version = d.policy_version;
                ALTER TABLE decisions DROP COLUMN policy_id;
                ALTER TABLE decisions DROP COLUMN policy_version;
                ALTER TABLE decisions RENAME COLUMN policy_ref TO policy_id;
                ALTER TABLE decisions ALTER COLUMN policy_id SET NOT NULL;

                ALTER TABLE actions ADD COLUMN provider_ref integer;
                UPDATE actions a SET provider_ref = ap.id FROM action_providers ap WHERE ap.provider_key = a.provider_id;
                ALTER TABLE actions DROP COLUMN provider_id;
                ALTER TABLE actions RENAME COLUMN provider_ref TO provider_id;
                ALTER TABLE actions ALTER COLUMN provider_id SET NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_raw_observations_source_id",
                table: "raw_observations",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_source_id",
                table: "events",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_policy_id",
                table: "decisions",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "IX_classifications_classifier_id",
                table: "classifications",
                column: "classifier_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_source_id",
                table: "audit_records",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_actions_provider_id",
                table: "actions",
                column: "provider_id");

            migrationBuilder.AddForeignKey(
                name: "FK_actions_action_providers_provider_id",
                table: "actions",
                column: "provider_id",
                principalTable: "action_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_audit_records_sources_source_id",
                table: "audit_records",
                column: "source_id",
                principalTable: "sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_classifications_classifiers_classifier_id",
                table: "classifications",
                column: "classifier_id",
                principalTable: "classifiers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_decisions_policies_policy_id",
                table: "decisions",
                column: "policy_id",
                principalTable: "policies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_events_sources_source_id",
                table: "events",
                column: "source_id",
                principalTable: "sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_raw_observations_sources_source_id",
                table: "raw_observations",
                column: "source_id",
                principalTable: "sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the descriptor strings onto the fact tables.  Dropping
            // the integer columns removes their foreign keys and indexes.
            migrationBuilder.Sql("""
                ALTER TABLE raw_observations ADD COLUMN source_key text;
                UPDATE raw_observations r SET source_key = s.source_key FROM sources s WHERE s.id = r.source_id;
                ALTER TABLE raw_observations DROP COLUMN source_id;
                ALTER TABLE raw_observations RENAME COLUMN source_key TO source_id;
                ALTER TABLE raw_observations ALTER COLUMN source_id SET NOT NULL;

                ALTER TABLE events ADD COLUMN source_key text;
                ALTER TABLE events ADD COLUMN source_type_text text;
                UPDATE events e SET source_key = s.source_key, source_type_text = COALESCE(s.source_type, 'unknown') FROM sources s WHERE s.id = e.source_id;
                ALTER TABLE events DROP COLUMN source_id;
                ALTER TABLE events RENAME COLUMN source_key TO source_id;
                ALTER TABLE events RENAME COLUMN source_type_text TO source_type;
                ALTER TABLE events ALTER COLUMN source_id SET NOT NULL;
                ALTER TABLE events ALTER COLUMN source_type SET NOT NULL;

                ALTER TABLE audit_records ADD COLUMN source_key text;
                UPDATE audit_records a SET source_key = s.source_key FROM sources s WHERE s.id = a.source_id;
                ALTER TABLE audit_records DROP COLUMN source_id;
                ALTER TABLE audit_records RENAME COLUMN source_key TO source_id;

                ALTER TABLE classifications ADD COLUMN classifier_key text;
                UPDATE classifications c SET classifier_key = k.classifier_key FROM classifiers k WHERE k.id = c.classifier_id;
                ALTER TABLE classifications DROP COLUMN classifier_id;
                ALTER TABLE classifications RENAME COLUMN classifier_key TO classifier_id;
                ALTER TABLE classifications ALTER COLUMN classifier_id SET NOT NULL;

                ALTER TABLE decisions ADD COLUMN policy_key text;
                ALTER TABLE decisions ADD COLUMN policy_version_text text;
                UPDATE decisions d SET policy_key = p.policy_key, policy_version_text = p.policy_version FROM policies p WHERE p.id = d.policy_id;
                ALTER TABLE decisions DROP COLUMN policy_id;
                ALTER TABLE decisions RENAME COLUMN policy_key TO policy_id;
                ALTER TABLE decisions RENAME COLUMN policy_version_text TO policy_version;
                ALTER TABLE decisions ALTER COLUMN policy_id SET NOT NULL;
                ALTER TABLE decisions ALTER COLUMN policy_version SET NOT NULL;

                ALTER TABLE actions ADD COLUMN provider_key text;
                UPDATE actions a SET provider_key = ap.provider_key FROM action_providers ap WHERE ap.id = a.provider_id;
                ALTER TABLE actions DROP COLUMN provider_id;
                ALTER TABLE actions RENAME COLUMN provider_key TO provider_id;
                ALTER TABLE actions ALTER COLUMN provider_id SET NOT NULL;
                """);

            migrationBuilder.DropTable(
                name: "action_providers");

            migrationBuilder.DropTable(
                name: "classifiers");

            migrationBuilder.DropTable(
                name: "policies");

            migrationBuilder.DropTable(
                name: "sources");

            migrationBuilder.CreateIndex(
                name: "IX_raw_observations_source_id",
                table: "raw_observations",
                column: "source_id");
        }
    }
}
