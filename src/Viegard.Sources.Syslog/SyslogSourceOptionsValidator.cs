using System.Net;
using Microsoft.Extensions.Options;
using Viegard.Application.Net;

namespace Viegard.Sources.Syslog;

/// <summary>Validates syslog listener configuration at startup; fail-closed on any doubt.</summary>
public sealed class SyslogSourceOptionsValidator : IValidateOptions<SyslogSourceOptions>
{
    public ValidateOptionsResult Validate(string? name, SyslogSourceOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (!IPAddress.TryParse(options.ListenAddress, out _))
        {
            failures.Add($"Syslog: ListenAddress '{options.ListenAddress}' is not a valid IP address.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add("Syslog: Port must be within 1-65535.");
        }

        if (options.AllowedSources.Count == 0)
        {
            failures.Add("Syslog: AllowedSources must be non-empty when the listener is enabled (fail-closed guardrail, D-0023).");
        }

        foreach (var source in options.AllowedSources)
        {
            if (!CidrSet.TryParseEntry(source, out var failure))
            {
                failures.Add($"Syslog: allowed source '{source}' is not a valid IP address or CIDR range: {failure}");
            }
        }

        if (options.MaxDatagramBytes < 128)
        {
            failures.Add("Syslog: MaxDatagramBytes must be at least 128.");
        }

        if (options.MaxDatagramsPerSourcePerSecond < 1)
        {
            failures.Add("Syslog: MaxDatagramsPerSourcePerSecond must be at least 1.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
