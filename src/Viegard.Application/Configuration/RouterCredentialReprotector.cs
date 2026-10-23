namespace Viegard.Application.Configuration;

public sealed record RouterCredentialReprotectResult(int Reprotected, int Failed, int AlreadyCurrent);

/// <summary>
/// Re-encrypts legacy router credentials (authentication bound to the router id only) so they bind the
/// router id, base URL, username, and transport mode.  Safe to run repeatedly; current-format values are skipped.
/// </summary>
public static class RouterCredentialReprotector
{
    public static async Task<RouterCredentialReprotectResult> RunAsync(
        IMikroTikRouterStore routers,
        IRouterCredentialProtector protector,
        Action<MikroTikRouter, string>? onFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routers);
        ArgumentNullException.ThrowIfNull(protector);

        var reprotected = 0;
        var failed = 0;
        var current = 0;
        foreach (var router in await routers.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var ciphertext = await routers.GetCredentialCiphertextAsync(router.Id, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ciphertext) || !protector.IsLegacyFormat(ciphertext))
            {
                current++;
                continue;
            }

            try
            {
                var password = protector.Unprotect(router, ciphertext);
                var upgraded = protector.Protect(router, password);
                var result = await routers.UpdateAsync(router, router.RowVersion, upgraded, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    reprotected++;
                }
                else
                {
                    failed++;
                    onFailure?.Invoke(router, $"Router update returned {result.Status}; it will be retried on the next start.");
                }
            }
            catch (RouterCredentialProtectionException ex)
            {
                failed++;
                onFailure?.Invoke(router, ex.Message);
            }
        }

        return new RouterCredentialReprotectResult(reprotected, failed, current);
    }
}
