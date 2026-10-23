using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.Options;
using Npgsql;

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

    /// <summary>
    /// Npgsql connection-pool cap.  Must fit inside the database role's
    /// CONNECTION LIMIT with headroom for LISTEN connections; satellite
    /// installs set a small value because satellite roles are
    /// connection-limited.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// TLS mode (D-0062).  When unset, <see cref="EffectiveSslMode"/> applies:
    /// <c>Prefer</c> for loopback hosts and the compose service name
    /// <c>viegard-db</c> (private compose network), otherwise
    /// <c>VerifyFull</c> so a remote host fails closed.  Remote hosts may
    /// not use Disable, Allow, or Prefer.
    /// </summary>
    public SslMode? SslMode { get; set; }

    /// <summary>
    /// Optional path to a PEM root CA certificate used to validate the server
    /// certificate (VerifyCA and VerifyFull only).  A path, never the
    /// certificate content.
    /// </summary>
    public string? RootCertificate { get; set; }

    /// <summary>The compose service name that is private to the compose network.</summary>
    public const string ComposeServiceHost = "viegard-db";

    public SslMode EffectiveSslMode =>
        SslMode ?? (IsPrivateHost(Host) ? Npgsql.SslMode.Prefer : Npgsql.SslMode.VerifyFull);

    /// <summary>True for loopback addresses and the compose service name, where traffic does not leave the host or compose network.</summary>
    public static bool IsPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim().Trim('[', ']');
        return string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, ComposeServiceHost, StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address));
    }
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

        if (options.MaxPoolSize is < 1 or > 1024)
        {
            failures.Add("Database: MaxPoolSize must be within 1-1024.");
        }

        if (options.SslMode is { } sslMode)
        {
            if (!Enum.IsDefined(sslMode))
            {
                failures.Add("Database: SslMode must be one of Disable, Allow, Prefer, Require, VerifyCA, VerifyFull.");
            }
            else if (!DatabaseOptions.IsPrivateHost(options.Host)
                && sslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
            {
                failures.Add("Database: SslMode Disable, Allow, and Prefer are only permitted for loopback hosts and the compose service name; use Require, VerifyCA, or VerifyFull for a remote host.");
            }
        }

        if (options.RootCertificate is not null)
        {
            if (string.IsNullOrWhiteSpace(options.RootCertificate) || options.RootCertificate.Any(char.IsControl))
            {
                failures.Add("Database: RootCertificate must be a file path without control characters.");
            }
            else if (options.EffectiveSslMode is not (SslMode.VerifyCA or SslMode.VerifyFull))
            {
                failures.Add("Database: RootCertificate requires SslMode VerifyCA or VerifyFull.");
            }
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
