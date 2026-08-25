using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Viegard.Persistence.Postgres;

/// <summary>Database connection configuration.  The password is a named secret (D-0006), never configuration.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Viegard:Database";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 5432;

    public string Name { get; set; } = "viegard";

    public string Username { get; set; } = "viegard";

    /// <summary>
    /// Schema for all Viegard objects (tables, queues, EF history), applied
    /// via the connection search path.  Lowercase letters, digits, and
    /// underscores only: the name participates in DDL.
    /// </summary>
    public string Schema { get; set; } = "viegard";

    /// <summary>Secret name resolved via ISecretProvider.</summary>
    public string PasswordSecretName { get; set; } = "viegard-db-password";

    /// <summary>Apply pending EF Core migrations at pipeline-host startup.</summary>
    public bool AutoMigrate { get; set; } = true;
}

public sealed partial class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add("Database: Host is required when the postgres persistence provider is selected.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add("Database: Port must be within 1-65535.");
        }

        if (string.IsNullOrWhiteSpace(options.Name) || string.IsNullOrWhiteSpace(options.Username))
        {
            failures.Add("Database: Name and Username are required.");
        }

        // Fail-closed: the schema name is interpolated into DDL, so only a
        // strictly safe identifier shape is accepted.
        if (!IsSafeSchemaName(options.Schema))
        {
            failures.Add("Database: Schema must match ^[a-z][a-z0-9_]*$ and be at most 63 characters.");
        }

        if (string.IsNullOrWhiteSpace(options.PasswordSecretName))
        {
            failures.Add("Database: PasswordSecretName is required (the secret name, never the password).");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    /// <summary>Shared safety check for schema names that participate in DDL.</summary>
    public static bool IsSafeSchemaName(string? schema) =>
        !string.IsNullOrWhiteSpace(schema)
        && schema.Length <= 63
        && SchemaNamePattern().IsMatch(schema);

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SchemaNamePattern();
}
