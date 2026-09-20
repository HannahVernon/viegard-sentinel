using Microsoft.Extensions.Options;

namespace Viegard.Application.Configuration;

/// <summary>Bootstrap configuration for the JetPack allowlist feed.</summary>
public sealed class JetPackFeedOptions
{
    public const string SectionName = "Viegard:JetPack";

    public string FeedUrl { get; set; } = JetPackFeedSettings.DefaultFeedUrl;

    public TimeSpan FetchInterval { get; set; } = JetPackFeedSettings.DefaultFetchInterval;

    public bool Enabled { get; set; } = true;

    public string AddressListName { get; set; } = JetPackFeedSettings.DefaultAddressListName;
}

public sealed class JetPackFeedOptionsValidator : IValidateOptions<JetPackFeedOptions>
{
    public ValidateOptionsResult Validate(string? name, JetPackFeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var candidate = JetPackFeedSettings.FromOptions(options, DateTimeOffset.UtcNow);
        return JetPackFeedSettingsValidator.TryValidate(candidate, out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}
