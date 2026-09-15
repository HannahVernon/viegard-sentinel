using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Viegard.Application.Configuration;

public interface ISatelliteRoleStore
{
    ValueTask<IReadOnlyList<SatelliteRoleInfo>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<SatelliteRoleSecret> CreateAsync(string satelliteName, CancellationToken cancellationToken = default);

    ValueTask<SatelliteRoleSecret> RotatePasswordAsync(string satelliteName, CancellationToken cancellationToken = default);

    ValueTask RevokeAsync(string satelliteName, CancellationToken cancellationToken = default);
}

public sealed record SatelliteRoleInfo(
    string SatelliteName,
    string RoleName,
    bool CanLogin,
    SatelliteConnectionFacts ConnectionFacts);

public sealed record SatelliteRoleSecret(
    string SatelliteName,
    string RoleName,
    string Password,
    SatelliteConnectionFacts ConnectionFacts,
    string GrantSummary);

public sealed record SatelliteConnectionFacts(
    string HostNote,
    int Port,
    string Database,
    string Username,
    string Schema)
{
    public string Hint =>
        $"{HostNote}; port {Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}; database {Database}; username {Username}; schema {Schema}";
}

public sealed record SatelliteRoleName
{
    public const string RolePrefix = "viegard_sat_";
    public const int MaxNameLength = 32;
    public const string ValidationError =
        "Satellite name must be 1-32 characters using only lowercase letters, digits, and underscores.";

    private SatelliteRoleName(string satelliteName)
    {
        SatelliteName = satelliteName;
    }

    public string SatelliteName { get; }

    public string RoleName => RolePrefix + SatelliteName;

    public static bool TryNormalize(
        string? candidate,
        [NotNullWhen(true)] out SatelliteRoleName? satelliteRoleName,
        out string error)
    {
        satelliteRoleName = null;
        error = string.Empty;

        var normalized = candidate?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaxNameLength)
        {
            error = ValidationError;
            return false;
        }

        foreach (var character in normalized)
        {
            var valid = character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character == '_';
            if (!valid)
            {
                error = ValidationError;
                return false;
            }
        }

        satelliteRoleName = new SatelliteRoleName(normalized);
        return true;
    }

    public static bool TryFromRoleName(
        string? roleName,
        [NotNullWhen(true)] out SatelliteRoleName? satelliteRoleName,
        out string error)
    {
        satelliteRoleName = null;
        error = string.Empty;

        var normalized = roleName?.Trim() ?? string.Empty;
        if (!normalized.StartsWith(RolePrefix, StringComparison.Ordinal))
        {
            error = $"Satellite role names must start with {RolePrefix}.";
            return false;
        }

        return TryNormalize(normalized[RolePrefix.Length..], out satelliteRoleName, out error);
    }
}

public static class SatelliteRolePassword
{
    public const int DefaultLength = 48;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Generate(int length = DefaultLength)
    {
        if (length < 32)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Satellite role passwords must be at least 32 characters.");
        }

        return string.Create(length, Alphabet, static (buffer, alphabet) =>
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }

    public static bool UsesAlphabet(string password)
    {
        foreach (var character in password)
        {
            var valid = character is >= 'A' and <= 'Z'
                || character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class SatelliteRoleStoreException : Exception
{
    public SatelliteRoleStoreException(string message)
        : base(message)
    {
    }
}
