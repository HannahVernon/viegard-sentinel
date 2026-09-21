using Microsoft.Extensions.Options;

namespace Viegard.Application.Configuration;

/// <summary>Bootstrap configuration for the local-model advisor.</summary>
public sealed class LocalModelAdvisorOptions
{
    public const string SectionName = "Viegard:LocalModelAdvisor";

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = LocalModelAdvisorSettings.DefaultEndpoint;

    public string Model { get; set; } = LocalModelAdvisorSettings.DefaultModel;

    public double Temperature { get; set; }

    public int TimeoutMs { get; set; } = 8000;

    public string KeepAlive { get; set; } = LocalModelAdvisorSettings.DefaultKeepAlive;

    public double InvokeConfidenceMin { get; set; } = 0.50;

    public double InvokeConfidenceMax { get; set; } = 0.85;

    public int MaxSeverityDelta { get; set; } = 3;

    public double MaxConfidenceDelta { get; set; } = 0.20;

    public bool ResponseCacheEnabled { get; set; }

    public int ResponseCacheTtlHours { get; set; } = 72;

    public bool EnsembleEnabled { get; set; }

    public string SecondModelEndpoint { get; set; } = LocalModelAdvisorSettings.DefaultSecondModelEndpoint;

    public string SecondModel { get; set; } = string.Empty;
}

public sealed class LocalModelAdvisorOptionsValidator : IValidateOptions<LocalModelAdvisorOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalModelAdvisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var candidate = LocalModelAdvisorSettings.FromOptions(options, DateTimeOffset.UtcNow);
        return LocalModelAdvisorSettingsValidator.TryValidate(candidate, out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}
