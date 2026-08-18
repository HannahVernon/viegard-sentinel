using System.Text.RegularExpressions;

namespace Viegard.Application.Secrets;

/// <summary>
/// Reads secrets from individual files in a directory mounted into the
/// container at runtime (production mechanism per D-0006).  The file name is
/// the secret name; a single trailing newline is trimmed.
/// </summary>
public sealed partial class FileSecretProvider : ISecretProvider
{
    private readonly string _directory;

    public FileSecretProvider(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public async Task<Secret?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);

        var path = Path.Combine(_directory, name);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return new Secret(name, value.TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// Secret names must be simple identifiers.  This prevents path traversal
    /// (e.g., "../../etc/passwd") from ever reaching the filesystem.
    /// </summary>
    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!SecretNamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                "Secret names may contain only letters, digits, hyphens, underscores, and non-leading dots.",
                nameof(name));
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-][A-Za-z0-9._-]*$")]
    private static partial Regex SecretNamePattern();
}
