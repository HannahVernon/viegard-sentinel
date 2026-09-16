using Microsoft.Extensions.Options;
using Npgsql;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres;

public sealed class PostgresSatelliteRoleStore(
    NpgsqlDataSource dataSource,
    IOptions<DatabaseOptions> options) : ISatelliteRoleStore
{
    private const string SatelliteRoleLikePattern = @"viegard\_sat\_%";
    private readonly DatabaseOptions _options = options.Value;

    public async ValueTask<IReadOnlyList<SatelliteRoleInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rolname, rolcanlogin
            FROM pg_catalog.pg_roles
            WHERE rolname LIKE @pattern ESCAPE '\'
            ORDER BY rolname
            """;
        command.Parameters.AddWithValue("pattern", SatelliteRoleLikePattern);

        var roles = new List<SatelliteRoleInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var roleName = reader.GetString(0);
            var satelliteName = roleName.StartsWith(SatelliteRoleName.RolePrefix, StringComparison.Ordinal)
                ? roleName[SatelliteRoleName.RolePrefix.Length..]
                : roleName;
            roles.Add(new SatelliteRoleInfo(
                satelliteName,
                roleName,
                reader.GetBoolean(1),
                BuildFacts(roleName)));
        }

        return roles;
    }

    public async ValueTask<SatelliteRoleSecret> CreateAsync(string satelliteName, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(satelliteName);
        var password = SatelliteRolePassword.Generate();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrowIfRoleExistsAsync(connection, transaction, normalized.RoleName, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BuildCreateSql(normalized.RoleName, password);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SatelliteRoleStoreException)
        {
            await RollBackIfPossibleAsync(transaction).ConfigureAwait(false);
            throw MapPostgresException(ex, normalized.RoleName);
        }

        return new SatelliteRoleSecret(
            normalized.SatelliteName,
            normalized.RoleName,
            password,
            BuildFacts(normalized.RoleName),
            BuildGrantSummary());
    }

    public async ValueTask<SatelliteRoleSecret> RotatePasswordAsync(string satelliteName, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(satelliteName);
        var password = SatelliteRolePassword.Generate();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrowIfRoleMissingAsync(connection, transaction, normalized.RoleName, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // ALTER ROLE cannot parameterize the role identifier or
            // PASSWORD clause.  The role name is built from the validated
            // satellite name and double-quoted; the password is generated
            // server-side from the alphanumeric-only alphabet.
            command.CommandText = $"ALTER ROLE {QuoteIdentifier(normalized.RoleName)} PASSWORD '{password}'";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SatelliteRoleStoreException)
        {
            await RollBackIfPossibleAsync(transaction).ConfigureAwait(false);
            throw MapPostgresException(ex, normalized.RoleName);
        }

        return new SatelliteRoleSecret(
            normalized.SatelliteName,
            normalized.RoleName,
            password,
            BuildFacts(normalized.RoleName),
            "Password rotated; grants unchanged.");
    }

    public async ValueTask RevokeAsync(string satelliteName, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(satelliteName);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrowIfRoleMissingAsync(connection, transaction, normalized.RoleName, cancellationToken).ConfigureAwait(false);
            var activeConnections = await CountActiveConnectionsAsync(connection, transaction, normalized.RoleName, cancellationToken)
                .ConfigureAwait(false);
            if (activeConnections > 0)
            {
                throw new SatelliteRoleStoreException(
                    $"PostgreSQL reports role {normalized.RoleName} has {activeConnections.ToString(System.Globalization.CultureInfo.InvariantCulture)} active connection(s).  Stop the satellite and try again before revoking.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // DROP OWNED BY and DROP ROLE cannot parameterize identifiers.
            // The role name is built from the validated satellite name and
            // double-quoted; no other dynamic SQL fragments are used here.
            command.CommandText = $"""
                DROP OWNED BY {QuoteIdentifier(normalized.RoleName)};
                DROP ROLE {QuoteIdentifier(normalized.RoleName)};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SatelliteRoleStoreException)
        {
            await RollBackIfPossibleAsync(transaction).ConfigureAwait(false);
            throw MapPostgresException(ex, normalized.RoleName);
        }
    }

    private SatelliteConnectionFacts BuildFacts(string roleName) => new(
        "Use the VM address or DNS name reachable from the satellite",
        _options.Port,
        _options.Name,
        roleName,
        _options.Schema);

    private string BuildGrantSummary() =>
        $"LOGIN, connection limit 16, USAGE on schema {_options.Schema}, table SELECT/INSERT/UPDATE/DELETE, sequence USAGE/SELECT, and matching future-object default privileges for the application database user.";

    private string BuildCreateSql(string roleName, string password)
    {
        var schema = SafeSchema();
        var quotedRole = QuoteIdentifier(roleName);
        var quotedSchema = QuoteIdentifier(schema);

        // PostgreSQL role DDL cannot parameterize identifiers or PASSWORD
        // clauses.  The interpolated fragments below are constrained before
        // this method is called: roleName is built from a fixed prefix plus a
        // character-by-character validated lowercase/digit/underscore
        // satellite name, schema is the startup-validated fail-closed
        // Viegard:Database:Schema value and is rechecked here, and password
        // is generated server-side from an alphanumeric-only alphabet.  The
        // identifiers are always double-quoted and the password is
        // single-quoted after construction.
        return $"""
            CREATE ROLE {quotedRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT CONNECTION LIMIT 16;
            GRANT USAGE ON SCHEMA {quotedSchema} TO {quotedRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {quotedSchema} TO {quotedRole};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {quotedSchema} TO {quotedRole};
            ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA {quotedSchema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {quotedRole};
            ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA {quotedSchema} GRANT USAGE, SELECT ON SEQUENCES TO {quotedRole};
            """;
    }

    private string SafeSchema()
    {
        if (!DatabaseOptionsValidator.IsSafeSchemaName(_options.Schema))
        {
            throw new SatelliteRoleStoreException("The configured database schema name is not safe for satellite role DDL.");
        }

        return _options.Schema;
    }

    private static SatelliteRoleName Normalize(string satelliteName)
    {
        if (!SatelliteRoleName.TryNormalize(satelliteName, out var normalized, out var error))
        {
            throw new SatelliteRoleStoreException(error);
        }

        return normalized;
    }

    private static async ValueTask ThrowIfRoleExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roleName,
        CancellationToken cancellationToken)
    {
        if (await RoleExistsAsync(connection, transaction, roleName, cancellationToken).ConfigureAwait(false))
        {
            throw new SatelliteRoleStoreException($"Satellite role {roleName} already exists.");
        }
    }

    private static async ValueTask ThrowIfRoleMissingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roleName,
        CancellationToken cancellationToken)
    {
        if (!await RoleExistsAsync(connection, transaction, roleName, cancellationToken).ConfigureAwait(false))
        {
            throw new SatelliteRoleStoreException($"Satellite role {roleName} was not found.");
        }
    }

    private static async ValueTask<bool> RoleExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roleName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @roleName)";
        command.Parameters.AddWithValue("roleName", roleName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL did not return a role-existence result."));
    }

    private static async ValueTask<long> CountActiveConnectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roleName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM pg_catalog.pg_stat_activity
            WHERE usename = @roleName
              AND pid <> pg_backend_pid()
            """;
        command.Parameters.AddWithValue("roleName", roleName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    private static async ValueTask RollBackIfPossibleAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (PostgresException)
        {
        }
    }

    private static SatelliteRoleStoreException MapPostgresException(Exception exception, string roleName)
    {
        if (exception is PostgresException postgresException)
        {
            return postgresException.SqlState switch
            {
                PostgresErrorCodes.InsufficientPrivilege => new SatelliteRoleStoreException(
                    "The application's database role lacks CREATEROLE; run the documented psql fallback or grant CREATEROLE."),
                PostgresErrorCodes.DuplicateObject => new SatelliteRoleStoreException(
                    $"Satellite role {roleName} already exists."),
                _ => new SatelliteRoleStoreException(
                    $"PostgreSQL rejected the satellite role change: {postgresException.MessageText}"),
            };
        }

        return new SatelliteRoleStoreException($"Satellite role change failed: {exception.Message}");
    }
}
