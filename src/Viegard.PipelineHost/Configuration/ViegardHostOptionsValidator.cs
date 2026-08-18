using Microsoft.Extensions.Options;

namespace Viegard.PipelineHost.Configuration;

/// <summary>Validates host role configuration at startup; the host refuses to start on invalid topology input.</summary>
public sealed class ViegardHostOptionsValidator : IValidateOptions<ViegardHostOptions>
{
    public ValidateOptionsResult Validate(string? name, ViegardHostOptions options)
    {
        if (options.Roles.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                $"At least one pipeline role must be configured under '{ViegardHostOptions.SectionName}:Roles'.");
        }

        var unknown = options.Roles
            .Where(r => !ViegardHostOptions.KnownRoles.Contains(r))
            .ToList();

        if (unknown.Count > 0)
        {
            return ValidateOptionsResult.Fail(
                $"Unknown pipeline role(s): {string.Join(", ", unknown)}.  " +
                $"Known roles: {string.Join(", ", ViegardHostOptions.KnownRoles)}.");
        }

        var duplicates = options.Roles
            .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        return duplicates.Count > 0
            ? ValidateOptionsResult.Fail($"Duplicate pipeline role(s): {string.Join(", ", duplicates)}.")
            : ValidateOptionsResult.Success;
    }
}
