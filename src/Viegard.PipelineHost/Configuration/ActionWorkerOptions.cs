using Microsoft.Extensions.Options;

namespace Viegard.PipelineHost.Configuration;

public sealed class ActionWorkerOptions
{
    public const string SectionName = "Viegard:Actions";

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class ActionWorkerOptionsValidator : IValidateOptions<ActionWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, ActionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromSeconds(30))
        {
            failures.Add("Action retry delay must be between 0 and 30 seconds.");
        }

        if (options.ReconciliationInterval < TimeSpan.FromMinutes(1)
            || options.ReconciliationInterval > TimeSpan.FromMinutes(60))
        {
            failures.Add("Action reconciliation interval must be between 1 and 60 minutes.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
