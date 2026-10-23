using System.Text;

namespace Viegard.Persistence.Postgres;

/// <summary>
/// Least-privilege grant matrix for satellite roles (D-0063).  A satellite
/// runs only the <c>sources</c> host role; the matrix below is what that role
/// reads and writes (derivation and evidence: docs/satellite-windows.md).
/// Every table in the EF model must appear in exactly one of
/// <see cref="Granted"/> or <see cref="Denied"/>; a database-free test scans
/// the model snapshot so a new table cannot ship unclassified.
/// </summary>
public static class SatelliteGrantPolicy
{
    /// <summary>A column-limited privilege, for example UPDATE on specific columns.</summary>
    public sealed record ColumnGrant(string Privilege, IReadOnlyList<string> Columns);

    public sealed record TableGrant(
        string Table,
        IReadOnlyList<string> TablePrivileges,
        IReadOnlyList<ColumnGrant> ColumnGrants,
        string? IdentityColumn = null);

    public static IReadOnlyList<TableGrant> Granted { get; } =
    [
        new("sources", ["SELECT", "INSERT"], [new("UPDATE", ["source_type"])], IdentityColumn: "id"),
        new("raw_observations", ["INSERT"], [new("SELECT", ["payload_reference"])]),
        new("events", ["INSERT"], []),
        new("audit_records", ["INSERT"], []),
        new("queue_messages", ["SELECT", "INSERT"], [], IdentityColumn: "id"),
        new("queue_counters", ["SELECT", "INSERT"], [new("UPDATE", ["enqueued"])]),
        new("queue_telemetry", ["SELECT", "INSERT", "UPDATE"], []),
        new("source_offsets", ["SELECT", "INSERT"], [new("UPDATE", ["value"])]),
        new("ingestion_filters", ["SELECT"], []),
        new(
            "instance_registry",
            ["INSERT"],
            [
                new("SELECT", ["instance_id"]),
                new("UPDATE", ["commit_sha", "host_name", "reported_at", "roles", "started_at", "upgrade_target", "version"]),
            ]),
        new(
            "host_upgrade_commands",
            ["SELECT"],
            [new("UPDATE", ["status", "started_at", "finished_at", "detail"])]),
    ];

    /// <summary>Tables a satellite must never touch.  Kept explicit so an unclassified table fails a test.</summary>
    public static IReadOnlyList<string> Denied { get; } =
    [
        "action_providers",
        "actions",
        "active_bans",
        "admin_errors",
        "admin_recovery_codes",
        "admin_sessions",
        "admin_totp_secrets",
        "admin_user_preferences",
        "admin_users",
        "admin_webauthn_credentials",
        "app_passwords",
        "burst_cooldowns",
        "burst_detection_settings",
        "burst_windows",
        "classifications",
        "classifier_settings",
        "classifiers",
        "corrections",
        "custom_signatures",
        "decisions",
        "doh_blocklist_settings",
        "doh_desired_addresses",
        "doh_probe_results",
        "doh_probe_summary",
        "doh_reconciliation_proposal",
        "incident_coalescing_settings",
        "incidents",
        "jetpack_desired_addresses",
        "jetpack_feed_settings",
        "local_model_advisor_category_bands",
        "local_model_advisor_consults",
        "local_model_advisor_injection_patterns",
        "local_model_advisor_prompt_templates",
        "local_model_advisor_response_cache",
        "local_model_advisor_settings",
        "mikrotik_routers",
        "policies",
        "policy_posture_settings",
        "policy_threshold_settings",
        "retention_settings",
        "session_security_settings",
    ];

    /// <summary>
    /// Builds the statements that reset a satellite role to the matrix: a
    /// baseline REVOKE (so roles created before D-0063 lose the blanket
    /// grants and their default-privilege entries), then explicit grants.
    /// No ALTER DEFAULT PRIVILEGES ... GRANT is emitted, so tables added by
    /// later migrations are not granted automatically.
    /// Both arguments are raw names that must be safe identifiers (lowercase
    /// letters, digits, underscores); they are double-quoted for identifiers
    /// and single-quoted for the sequence-grant literals.
    /// </summary>
    public static string BuildGrantSql(string schema, string roleName)
    {
        if (!DatabaseOptionsValidator.IsSafeSchemaName(schema) || !DatabaseOptionsValidator.IsSafeSchemaName(roleName))
        {
            throw new ArgumentException("Schema and role names must be safe lowercase identifiers.");
        }

        var quotedSchema = Quote(schema);
        var quotedRole = Quote(roleName);
        var sql = new StringBuilder();

        sql.AppendLine($"REVOKE ALL ON ALL TABLES IN SCHEMA {quotedSchema} FROM {quotedRole};");
        sql.AppendLine($"REVOKE ALL ON ALL SEQUENCES IN SCHEMA {quotedSchema} FROM {quotedRole};");
        sql.AppendLine($"ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA {quotedSchema} REVOKE ALL ON TABLES FROM {quotedRole};");
        sql.AppendLine($"ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA {quotedSchema} REVOKE ALL ON SEQUENCES FROM {quotedRole};");
        sql.AppendLine($"GRANT USAGE ON SCHEMA {quotedSchema} TO {quotedRole};");

        foreach (var grant in Granted)
        {
            var target = $"{quotedSchema}.{Quote(grant.Table)}";
            if (grant.TablePrivileges.Count > 0)
            {
                sql.AppendLine($"GRANT {string.Join(", ", grant.TablePrivileges)} ON TABLE {target} TO {quotedRole};");
            }

            foreach (var columnGrant in grant.ColumnGrants)
            {
                var columns = string.Join(", ", columnGrant.Columns.Select(Quote));
                sql.AppendLine($"GRANT {columnGrant.Privilege} ({columns}) ON TABLE {target} TO {quotedRole};");
            }
        }

        // Identity sequence names are resolved by the server instead of
        // guessing the implicit name.  Only USAGE is needed for nextval().
        var identityTables = Granted
            .Where(grant => grant.IdentityColumn is not null)
            .Select(grant => $"ARRAY['{grant.Table}', '{grant.IdentityColumn}']");
        sql.AppendLine("DO $viegard_grants$");
        sql.AppendLine("DECLARE item text[];");
        sql.AppendLine("BEGIN");
        sql.AppendLine($"  FOREACH item SLICE 1 IN ARRAY ARRAY[{string.Join(", ", identityTables)}] LOOP");
        sql.AppendLine($"    EXECUTE format('GRANT USAGE ON SEQUENCE %s TO %I', pg_get_serial_sequence(format('%I.%I', '{schema}', item[1]), item[2]), '{roleName}');");
        sql.AppendLine("  END LOOP;");
        sql.AppendLine("END");
        sql.AppendLine("$viegard_grants$;");

        return sql.ToString();
    }

    private static string Quote(string identifier) => $"\"{identifier}\"";
}
