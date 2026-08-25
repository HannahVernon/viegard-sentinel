using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Viegard.Persistence.Postgres;

/// <summary>
/// Single place that configures the context against Npgsql so the runtime
/// hosts, the design-time factory, and the integration tests all agree on
/// the migrations-history location.  The history table is pinned to the
/// configured schema; every other object follows the connection search path.
/// </summary>
public static class ViegardDbContextConfiguration
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, NpgsqlDataSource dataSource, string schema) =>
        builder.UseNpgsql(dataSource, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", schema));

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString, string schema) =>
        builder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", schema));
}
