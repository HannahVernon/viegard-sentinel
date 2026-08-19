using Microsoft.Extensions.Options;

namespace Viegard.Sources.Imap;

/// <summary>Validates IMAP source configuration at startup; the host refuses to start on invalid input.</summary>
public sealed class ImapSourceOptionsValidator : IValidateOptions<ImapSourceOptions>
{
    public ValidateOptionsResult Validate(string? name, ImapSourceOptions options)
    {
        var failures = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in options.Accounts)
        {
            var id = string.IsNullOrWhiteSpace(account.AccountId) ? "(missing AccountId)" : account.AccountId;

            if (string.IsNullOrWhiteSpace(account.AccountId))
            {
                failures.Add("Every IMAP account requires a non-empty AccountId.");
            }
            else if (!seenIds.Add(account.AccountId))
            {
                failures.Add($"Duplicate IMAP AccountId '{account.AccountId}'.");
            }

            if (string.IsNullOrWhiteSpace(account.Host))
            {
                failures.Add($"IMAP account '{id}': Host is required.");
            }

            if (account.Port is < 1 or > 65535)
            {
                failures.Add($"IMAP account '{id}': Port must be within 1-65535.");
            }

            if (string.IsNullOrWhiteSpace(account.Username))
            {
                failures.Add($"IMAP account '{id}': Username is required.");
            }

            if (string.IsNullOrWhiteSpace(account.PasswordSecretName))
            {
                failures.Add($"IMAP account '{id}': PasswordSecretName is required (the secret name, never the password).");
            }

            if (account.AuthMechanism == ImapAuthMechanism.OAuth2)
            {
                failures.Add($"IMAP account '{id}': OAuth2 is a reserved seam and is not implemented yet (D-0019).");
            }

            if (account.Folders.Count == 0)
            {
                failures.Add($"IMAP account '{id}': at least one folder must be configured.");
            }

            if (account.PollInterval <= TimeSpan.Zero || account.IdleCycle <= TimeSpan.Zero || account.ReconnectDelay < TimeSpan.Zero)
            {
                failures.Add($"IMAP account '{id}': PollInterval and IdleCycle must be positive; ReconnectDelay must be non-negative.");
            }

            if (account.IdleFailureThreshold < 1)
            {
                failures.Add($"IMAP account '{id}': IdleFailureThreshold must be at least 1.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
