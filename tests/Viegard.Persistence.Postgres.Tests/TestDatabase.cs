using Npgsql;

namespace Viegard.Persistence.Postgres.Tests;

/// <summary>
/// Integration-test gate: these tests need a live PostgreSQL and run only
/// when VIEGARD_TEST_POSTGRES holds a connection string, e.g.
/// "Host=localhost;Port=5432;Database=viegard_test;Username=viegard_test;Password=(test-only)".
/// Never point this at a real instance; tests create and drop objects.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(TestDatabase.ConnectionString))
        {
            Skip = "VIEGARD_TEST_POSTGRES is not set; skipping PostgreSQL integration test.";
        }
    }
}

public static class TestDatabase
{
    /// <summary>Schema the tests run in, mirroring the runtime default.</summary>
    public const string Schema = "viegard";

    public static string? ConnectionString { get; } =
        Environment.GetEnvironmentVariable("VIEGARD_TEST_POSTGRES");

    public static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString
            ?? throw new InvalidOperationException("VIEGARD_TEST_POSTGRES is not set."))
        {
            SearchPath = Schema,
        };
        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }
}
