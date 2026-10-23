using System.Text.RegularExpressions;

namespace Viegard.Persistence.Postgres.Tests;

/// <summary>Database-free guards for the satellite grant matrix (D-0063).</summary>
public sealed partial class SatelliteGrantPolicyTests
{
    [Fact]
    public void Every_model_table_is_classified_as_granted_or_denied()
    {
        var modelTables = ReadModelTables();
        var classified = SatelliteGrantPolicy.Granted.Select(grant => grant.Table)
            .Concat(SatelliteGrantPolicy.Denied)
            .ToList();

        Assert.Empty(classified.GroupBy(table => table).Where(group => group.Count() > 1).Select(group => group.Key));
        Assert.Empty(modelTables.Except(classified, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Empty(classified.Except(modelTables, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Generated_sql_uses_explicit_grants_only()
    {
        var sql = SatelliteGrantPolicy.BuildGrantSql("viegard", "viegard_sat_a");

        Assert.DoesNotContain("ON ALL TABLES IN SCHEMA \"viegard\" TO", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ON ALL SEQUENCES IN SCHEMA \"viegard\" TO", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ALL", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON ALL TABLES IN SCHEMA \"viegard\" FROM \"viegard_sat_a\"", sql, StringComparison.Ordinal);

        var defaultPrivilegeLines = sql.Split('\n')
            .Where(line => line.Contains("ALTER DEFAULT PRIVILEGES", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(defaultPrivilegeLines);
        Assert.All(defaultPrivilegeLines, line =>
        {
            Assert.Contains(" REVOKE ALL ON ", line, StringComparison.Ordinal);
            Assert.DoesNotContain(" GRANT ", line, StringComparison.Ordinal);
        });

        foreach (var grant in SatelliteGrantPolicy.Granted)
        {
            Assert.Contains($"\"viegard\".\"{grant.Table}\" TO \"viegard_sat_a\";", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Generated_sql_never_references_denied_tables()
    {
        var sql = SatelliteGrantPolicy.BuildGrantSql("viegard", "viegard_sat_a");

        foreach (var table in SatelliteGrantPolicy.Denied)
        {
            Assert.DoesNotContain($"\"{table}\"", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Host_upgrade_commands_allows_select_and_column_limited_update_only()
    {
        var grant = Assert.Single(SatelliteGrantPolicy.Granted, candidate => candidate.Table == "host_upgrade_commands");
        Assert.Equal(["SELECT"], grant.TablePrivileges);
        var update = Assert.Single(grant.ColumnGrants);
        Assert.Equal("UPDATE", update.Privilege);
        Assert.Equal(["status", "started_at", "finished_at", "detail"], update.Columns);

        var sql = SatelliteGrantPolicy.BuildGrantSql("viegard", "viegard_sat_a");
        Assert.Contains(
            "GRANT UPDATE (\"status\", \"started_at\", \"finished_at\", \"detail\") ON TABLE \"viegard\".\"host_upgrade_commands\" TO \"viegard_sat_a\";",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_granted_table_carries_delete_or_all_privileges()
    {
        foreach (var grant in SatelliteGrantPolicy.Granted)
        {
            Assert.DoesNotContain("DELETE", grant.TablePrivileges);
            Assert.DoesNotContain("ALL", grant.TablePrivileges);
            Assert.DoesNotContain(grant.ColumnGrants, column => column.Privilege is "DELETE" or "ALL");
        }
    }

    [Theory]
    [InlineData("viegard\"; DROP ROLE x; --", "viegard_sat_a")]
    [InlineData("viegard", "viegard_sat_a'; --")]
    public void Unsafe_names_are_rejected(string schema, string role)
    {
        Assert.Throws<ArgumentException>(() => SatelliteGrantPolicy.BuildGrantSql(schema, role));
    }

    private static HashSet<string> ReadModelTables()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Viegard.Persistence.Postgres",
                "Migrations",
                "ViegardDbContextModelSnapshot.cs");
            if (File.Exists(candidate))
            {
                var tables = ToTablePattern().Matches(File.ReadAllText(candidate))
                    .Select(match => match.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.NotEmpty(tables);
                return tables;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("ViegardDbContextModelSnapshot.cs was not found above the test output directory.");
    }

    [GeneratedRegex(@"ToTable\(""([a-z_0-9]+)""")]
    private static partial Regex ToTablePattern();
}

public sealed class DatabaseTlsTests
{
    private readonly DatabaseOptionsValidator _validator = new();

    [Theory]
    [InlineData("viegard-db", Npgsql.SslMode.Prefer)]
    [InlineData("localhost", Npgsql.SslMode.Prefer)]
    [InlineData("127.0.0.1", Npgsql.SslMode.Prefer)]
    [InlineData("::1", Npgsql.SslMode.Prefer)]
    [InlineData("db.example.com", Npgsql.SslMode.VerifyFull)]
    [InlineData("192.0.2.10", Npgsql.SslMode.VerifyFull)]
    public void Default_ssl_mode_fails_closed_for_remote_hosts(string host, Npgsql.SslMode expected)
    {
        var options = new DatabaseOptions { Host = host };
        Assert.Equal(expected, options.EffectiveSslMode);
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(Npgsql.SslMode.Disable)]
    [InlineData(Npgsql.SslMode.Allow)]
    [InlineData(Npgsql.SslMode.Prefer)]
    public void Weak_ssl_modes_are_rejected_for_remote_hosts(Npgsql.SslMode mode)
    {
        var options = new DatabaseOptions { Host = "192.0.2.10", SslMode = mode };
        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Disable_is_allowed_for_the_compose_service_host()
    {
        var options = new DatabaseOptions { Host = "viegard-db", SslMode = Npgsql.SslMode.Disable };
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Undefined_ssl_mode_is_rejected()
    {
        var options = new DatabaseOptions { Host = "viegard-db", SslMode = (Npgsql.SslMode)99 };
        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Root_certificate_requires_a_verifying_mode()
    {
        Assert.True(_validator.Validate(null, new DatabaseOptions
        {
            Host = "viegard-db",
            SslMode = Npgsql.SslMode.Require,
            RootCertificate = "/certs/root.crt",
        }).Failed);
        Assert.True(_validator.Validate(null, new DatabaseOptions
        {
            Host = "192.0.2.10",
            RootCertificate = "/certs/root.crt",
        }).Succeeded);
        Assert.True(_validator.Validate(null, new DatabaseOptions
        {
            Host = "192.0.2.10",
            RootCertificate = " ",
        }).Failed);
    }

    [Fact]
    public void Connection_string_carries_ssl_mode_and_root_certificate()
    {
        var options = new DatabaseOptions
        {
            Host = "db.example.com",
            SslMode = Npgsql.SslMode.VerifyCA,
            RootCertificate = "C:\\viegard\\certs\\root.crt",
        };
        var builder = new Npgsql.NpgsqlConnectionStringBuilder { Host = options.Host };

        ServiceCollectionExtensions.ApplyTls(builder, options);

        Assert.Equal(Npgsql.SslMode.VerifyCA, builder.SslMode);
        Assert.Equal("C:\\viegard\\certs\\root.crt", builder.RootCertificate);
    }

    [Fact]
    public void Connection_string_defaults_to_verify_full_for_remote_hosts()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder();

        ServiceCollectionExtensions.ApplyTls(builder, new DatabaseOptions { Host = "db.example.com" });

        Assert.Equal(Npgsql.SslMode.VerifyFull, builder.SslMode);
        Assert.Null(builder.RootCertificate);
    }
}
