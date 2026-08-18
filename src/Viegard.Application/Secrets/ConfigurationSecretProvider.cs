using Microsoft.Extensions.Configuration;

namespace Viegard.Application.Secrets;

/// <summary>
/// Reads secrets from <see cref="IConfiguration"/> under a fixed section,
/// backed by .NET user-secrets in development (D-0006).  Never use ordinary
/// appsettings files or environment variables for real secrets.
/// </summary>
public sealed class ConfigurationSecretProvider : ISecretProvider
{
    public const string DefaultSectionName = "Viegard:Secrets";

    private readonly IConfiguration _configuration;
    private readonly string _sectionName;

    public ConfigurationSecretProvider(IConfiguration configuration, string sectionName = DefaultSectionName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        _configuration = configuration;
        _sectionName = sectionName;
    }

    public Task<Secret?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        FileSecretProvider.ValidateName(name);

        var value = _configuration[$"{_sectionName}:{name}"];
        return Task.FromResult(value is null ? null : new Secret(name, value));
    }
}
