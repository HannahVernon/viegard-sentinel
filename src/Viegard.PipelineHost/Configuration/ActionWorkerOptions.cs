using Microsoft.Extensions.Options;

namespace Viegard.PipelineHost.Configuration;

public sealed class ActionWorkerOptions
{
    public const string SectionName = "Viegard:Actions";

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class ActionWorkerOptionsValidator : IValidateOptions<ActionWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, ActionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromSeconds(30)
            ? ValidateOptionsResult.Fail("Action retry delay must be between 0 and 30 seconds.")
            : ValidateOptionsResult.Success;
    }
}
