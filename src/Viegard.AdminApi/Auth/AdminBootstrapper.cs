using Viegard.AdminApi.Configuration;
using Viegard.Application.Auth;
using Viegard.Application.Secrets;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Microsoft.Extensions.Options;

namespace Viegard.AdminApi.Auth;

public static class AdminBootstrapper
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IAdminUserStore>();
        if (await users.AnyUsersAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var options = scope.ServiceProvider.GetRequiredService<IOptions<AdminExposureOptions>>().Value;
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretProvider>();
        var passwordService = scope.ServiceProvider.GetRequiredService<AdminPasswordService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Viegard.AdminApi.Bootstrap");

        var initialPassword = await secrets.GetAsync(options.Bootstrap.PasswordSecretName, cancellationToken).ConfigureAwait(false);
        if (initialPassword is null)
        {
            logger.LogError(
                "No admin users exist and bootstrap password secret '{SecretName}' was not found.  Admin UI remains locked.",
                options.Bootstrap.PasswordSecretName);
            return;
        }

        if (!passwordService.ValidateNewPassword(initialPassword.Reveal(), out var passwordError))
        {
            logger.LogError("No admin users exist but the bootstrap password is invalid: {PasswordError}", passwordError);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var user = new AdminUser
        {
            Id = ViegardId.New(),
            Username = options.Bootstrap.Username,
            PasswordHash = string.Empty,
            PasswordChangedAt = now,
            FailedLoginCount = 0,
            LockedUntil = null,
            MustChangePassword = true,
            TotpEnrolled = false,
            CreatedAt = now,
        };

        user = user with { PasswordHash = passwordService.HashPassword(user, initialPassword.Reveal()) };
        await users.CreateAsync(user, cancellationToken).ConfigureAwait(false);
        logger.LogWarning(
            "Bootstrap admin user '{Username}' was created and must change password and enroll TOTP at first login.",
            user.Username);
    }
}
