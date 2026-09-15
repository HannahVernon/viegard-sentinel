using System.Collections.Concurrent;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

public sealed class InMemorySatelliteRoleStore : ISatelliteRoleStore
{
    private readonly ConcurrentDictionary<string, SatelliteRoleInfo> _roles = new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<SatelliteRoleInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SatelliteRoleInfo>>(
            _roles.Values.OrderBy(role => role.RoleName, StringComparer.Ordinal).ToList());

    public ValueTask<SatelliteRoleSecret> CreateAsync(string satelliteName, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(satelliteName);
        var password = SatelliteRolePassword.Generate();
        var facts = BuildFacts(normalized.RoleName);
        var info = new SatelliteRoleInfo(normalized.SatelliteName, normalized.RoleName, CanLogin: true, facts);
        if (!_roles.TryAdd(normalized.RoleName, info))
        {
            throw new SatelliteRoleStoreException($"Satellite role {normalized.RoleName} already exists.");
        }

        return ValueTask.FromResult(new SatelliteRoleSecret(
            normalized.SatelliteName,
            normalized.RoleName,
            password,
            facts,
            "In-memory development role with login enabled; schema DML grants are simulated."));
    }

    public ValueTask<SatelliteRoleSecret> RotatePasswordAsync(string satelliteName, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(satelliteName);
        if (!_roles.TryGetValue(normalized.RoleName, out var info))
        {
            throw new SatelliteRoleStoreException($"Satellite role {normalized.RoleName} was not found.");
        }

        var password = SatelliteRolePassword.Generate();
        return ValueTask.FromResult(new SatelliteRoleSecret(
            info.SatelliteName,
            info.RoleName,
            password,
            info.ConnectionFacts,
            "Password rotated; grants unchanged."));
    }

    public ValueTask RevokeAsync(string satelliteName, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(satelliteName);
        if (!_roles.TryRemove(normalized.RoleName, out _))
        {
            throw new SatelliteRoleStoreException($"Satellite role {normalized.RoleName} was not found.");
        }

        return ValueTask.CompletedTask;
    }

    private static SatelliteRoleName Normalize(string satelliteName)
    {
        if (!SatelliteRoleName.TryNormalize(satelliteName, out var normalized, out var error))
        {
            throw new SatelliteRoleStoreException(error);
        }

        return normalized;
    }

    private static SatelliteConnectionFacts BuildFacts(string roleName) => new(
        "Use the PostgreSQL host name or address reachable from the satellite",
        5432,
        "viegard",
        roleName,
        "viegard");
}
