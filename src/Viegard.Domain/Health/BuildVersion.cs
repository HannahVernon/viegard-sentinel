using System.Reflection;

namespace Viegard.Domain.Health;

public sealed record BuildVersionInfo(
    string InformationalVersion,
    string? CommitSha,
    string? ShortSha);

public static class BuildVersion
{
    public const string Unknown = "unknown";
    private const int FullShaLength = 40;
    private const int ShortShaLength = 9;

    public static BuildVersionInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return Parse(informationalVersion);
    }

    public static BuildVersionInfo Parse(string? informationalVersion)
    {
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? Unknown
            : informationalVersion;
        var plusIndex = version.LastIndexOf('+');
        if (plusIndex < 0 || plusIndex == version.Length - 1)
        {
            return new BuildVersionInfo(version, CommitSha: null, ShortSha: null);
        }

        var suffix = version[(plusIndex + 1)..];
        if (suffix.Length != FullShaLength || !suffix.All(IsHex))
        {
            return new BuildVersionInfo(version, CommitSha: null, ShortSha: null);
        }

        return new BuildVersionInfo(version, suffix, suffix[..ShortShaLength]);
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9'
        || value is >= 'a' and <= 'f'
        || value is >= 'A' and <= 'F';
}
