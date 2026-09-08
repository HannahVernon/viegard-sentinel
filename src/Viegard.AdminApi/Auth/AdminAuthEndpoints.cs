using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Viegard.Application.Auth;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Auth;

public static class AdminAuthEndpoints
{
    private const string UniformFailure = "Invalid username, password, or second factor.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapAdminAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/totp", CompleteSecondFactorAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/logout", LogoutAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/step-up", StepUpAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/sessions/revoke", RevokeSessionsAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/password/change", ChangePasswordAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/totp/enroll", EnrollTotpAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/recovery-codes/regenerate", RegenerateRecoveryCodesAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/webauthn/register/options", BeginWebAuthnRegistrationAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/webauthn/register", CompleteWebAuthnRegistrationAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/webauthn/assert/options", BeginWebAuthnAssertionAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/webauthn/assert", CompleteWebAuthnAssertionAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth");
        app.MapPost("/auth/webauthn/delete", DeleteWebAuthnCredentialAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        PendingTwoFactorCookie pendingCookie,
        AdminPasswordService passwords,
        AdminAuthAuditor auditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var username = form["username"].ToString();
        var password = form["password"].ToString();
        var user = await users.FindByUsernameAsync(username, context.RequestAborted).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (user is null || (user.LockedUntil is not null && user.LockedUntil > now))
        {
            // Unknown and locked accounts must cost the same as a real
            // verification: no username-existence timing oracle.
            passwords.BurnTimingEquivalent(password);
            await auditor.RecordAsync(
                AdminAuthEventKind.LoginFailed,
                username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/login", error: UniformFailure);
        }

        var verification = passwords.Verify(user, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            var failedCount = user.FailedLoginCount + 1;
            var lockedUntil = failedCount >= 10 ? now.AddMinutes(Math.Min(60, Math.Pow(2, failedCount - 10))) : (DateTimeOffset?)null;
            await users.UpdateAsync(user with
            {
                FailedLoginCount = failedCount,
                LockedUntil = lockedUntil,
            }, context.RequestAborted).ConfigureAwait(false);

            await auditor.RecordAsync(
                lockedUntil is null ? AdminAuthEventKind.LoginFailed : AdminAuthEventKind.LockoutTriggered,
                username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/login", error: UniformFailure);
        }

        var updatedUser = user with { FailedLoginCount = 0, LockedUntil = null };
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            updatedUser = updatedUser with
            {
                PasswordHash = passwords.HashPassword(updatedUser, password),
                PasswordChangedAt = now,
            };
        }

        await users.UpdateAsync(updatedUser, context.RequestAborted).ConfigureAwait(false);
        pendingCookie.Write(context, updatedUser.Id);
        return Results.Redirect("/login/2fa");
    }

    private static async Task<IResult> CompleteSecondFactorAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        PendingTwoFactorCookie pendingCookie,
        AdminPasswordService passwords,
        RecoveryCodesCookie recoveryCodesCookie,
        TotpService totp,
        RecoveryCodeService recoveryCodes,
        IOptions<AdminAuthOptions> options,
        AdminAuthAuditor auditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        if (!pendingCookie.TryRead(context, out var userId))
        {
            return Redirect("/login", error: UniformFailure);
        }

        var user = await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false);
        if (user is null)
        {
            pendingCookie.Clear(context);
            return Redirect("/login", error: UniformFailure);
        }

        // A lockout triggered mid-flow (including by second-factor failures)
        // also blocks the 2FA stage.
        if (user.LockedUntil is not null && user.LockedUntil > DateTimeOffset.UtcNow)
        {
            pendingCookie.Clear(context);
            await auditor.RecordAsync(
                AdminAuthEventKind.LoginFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/login", error: UniformFailure);
        }

        if (string.Equals(form["flow"].ToString(), "change-password", StringComparison.OrdinalIgnoreCase))
        {
            return await ChangePendingPasswordCoreAsync(context, users, passwords, auditor, user, form).ConfigureAwait(false);
        }

        if (user.MustChangePassword)
        {
            return Redirect("/login/2fa", error: "Change the bootstrap password before enrolling TOTP.");
        }

        var code = form["code"].ToString();
        var setupSecret = form["setupSecret"].ToString();
        var recoveryCode = form["recoveryCode"].ToString();
        var valid = false;
        var usedRecoveryCode = false;

        if (!user.TotpEnrolled)
        {
            long acceptedStep = 0;
            valid = !string.IsNullOrWhiteSpace(setupSecret)
                && TotpService.VerifyCode(setupSecret, code, null, DateTimeOffset.UtcNow, TotpService.RuntimeDigits, out acceptedStep);
            if (valid)
            {
                var enrolledAt = DateTimeOffset.UtcNow;
                await users.UpsertTotpSecretAsync(new AdminTotpSecret
                {
                    UserId = user.Id,
                    SecretBase32 = setupSecret,
                    LastAcceptedStep = acceptedStep,
                    EnrolledAt = enrolledAt,
                }, context.RequestAborted).ConfigureAwait(false);

                user = user with { TotpEnrolled = true };
                await users.UpdateAsync(user, context.RequestAborted).ConfigureAwait(false);
                var generatedCodes = recoveryCodes.GenerateCodes();
                await users.ReplaceRecoveryCodesAsync(
                    user.Id,
                    recoveryCodes.ToRows(user.Id, generatedCodes, enrolledAt),
                    context.RequestAborted).ConfigureAwait(false);
                recoveryCodesCookie.Write(context, generatedCodes);
                await auditor.RecordAsync(AdminAuthEventKind.TotpEnrolled, user.Username, context, cancellationToken: context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        else if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            var availableCodes = await users.GetRecoveryCodesAsync(user.Id, context.RequestAborted).ConfigureAwait(false);
            var match = availableCodes.FirstOrDefault(c => c.UsedAt is null && RecoveryCodeService.Verify(recoveryCode, c.CodeHash));
            valid = match is not null && await users.TryMarkRecoveryCodeUsedAsync(
                match.Id,
                DateTimeOffset.UtcNow,
                context.RequestAborted).ConfigureAwait(false);
            usedRecoveryCode = valid;
        }
        else
        {
            var secret = await users.GetTotpSecretAsync(user.Id, context.RequestAborted).ConfigureAwait(false);
            valid = secret is not null
                && totp.VerifyCode(secret.SecretBase32, code, secret.LastAcceptedStep, out var acceptedStep)
                && await users.TrySetTotpLastAcceptedStepAsync(user.Id, acceptedStep, context.RequestAborted).ConfigureAwait(false);
        }

        if (!valid)
        {
            // Second-factor failures count toward the same lockout as
            // password failures; an attacker holding the password must not
            // get an unmetered TOTP guessing budget.
            var failedCount = user.FailedLoginCount + 1;
            var lockedUntil = failedCount >= 10
                ? DateTimeOffset.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, failedCount - 10)))
                : (DateTimeOffset?)null;
            await users.UpdateAsync(user with
            {
                FailedLoginCount = failedCount,
                LockedUntil = lockedUntil,
            }, context.RequestAborted).ConfigureAwait(false);

            await auditor.RecordAsync(
                lockedUntil is null ? AdminAuthEventKind.TotpFailed : AdminAuthEventKind.LockoutTriggered,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/login/2fa", error: UniformFailure);
        }

        return await CompleteSuccessfulPendingSecondFactorAsync(
            context,
            users,
            sessions,
            pendingCookie,
            user,
            options.Value,
            auditor,
            usedRecoveryCode,
            "/account").ConfigureAwait(false);
    }

    private static async Task<IResult> ChangePendingPasswordCoreAsync(
        HttpContext context,
        IAdminUserStore users,
        AdminPasswordService passwords,
        AdminAuthAuditor auditor,
        AdminUser user,
        IFormCollection form)
    {
        var newPassword = form["newPassword"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return Redirect("/login/2fa", error: "Password confirmation did not match.");
        }

        if (!passwords.ValidateNewPassword(newPassword, out var error))
        {
            return Redirect("/login/2fa", error: error);
        }

        var updated = user with
        {
            PasswordHash = passwords.HashPassword(user, newPassword),
            PasswordChangedAt = DateTimeOffset.UtcNow,
            MustChangePassword = false,
            FailedLoginCount = 0,
            LockedUntil = null,
        };
        await users.UpdateAsync(updated, context.RequestAborted).ConfigureAwait(false);
        await auditor.RecordAsync(AdminAuthEventKind.PasswordChanged, updated.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return Redirect("/login/2fa", status: "Password changed.  Enroll TOTP to finish signing in.");
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor auditor)
    {
        await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (TryGetCurrentSessionId(context.User, out var sessionId))
        {
            await sessions.RevokeAsync(sessionId, DateTimeOffset.UtcNow, context.RequestAborted).ConfigureAwait(false);
            await auditor.RecordAsync(AdminAuthEventKind.SessionRevoked, user?.Username, context, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        return Results.Redirect("/login?status=Signed%20out.");
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        AdminPasswordService passwords,
        AdminAuthAuditor auditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var currentPassword = form["currentPassword"].ToString();
        var newPassword = form["newPassword"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();
        if (passwords.Verify(user, currentPassword) == PasswordVerificationResult.Failed)
        {
            return Redirect("/account", error: "Current password was not accepted.");
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return Redirect("/account", error: "Password confirmation did not match.");
        }

        if (!passwords.ValidateNewPassword(newPassword, out var error))
        {
            return Redirect("/account", error: error);
        }

        var updated = user with
        {
            PasswordHash = passwords.HashPassword(user, newPassword),
            PasswordChangedAt = DateTimeOffset.UtcNow,
            MustChangePassword = false,
        };
        await users.UpdateAsync(updated, context.RequestAborted).ConfigureAwait(false);
        await auditor.RecordAsync(AdminAuthEventKind.PasswordChanged, updated.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return Redirect("/account", status: "Password changed.");
    }

    private static async Task<IResult> EnrollTotpAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        RecoveryCodeService recoveryCodes,
        RecoveryCodesCookie recoveryCodesCookie,
        AdminAuthAuditor auditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!await HasRecentStepUpAsync(context, context.RequestServices.GetRequiredService<IAdminSessionStore>()).ConfigureAwait(false))
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/account", error: "Step-up verification is required before changing TOTP enrollment.");
        }

        var secret = form["setupSecret"].ToString();
        var code = form["code"].ToString();
        if (!TotpService.VerifyCode(secret, code, null, DateTimeOffset.UtcNow, TotpService.RuntimeDigits, out var acceptedStep))
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.TotpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/account", error: UniformFailure);
        }

        var now = DateTimeOffset.UtcNow;
        await users.UpsertTotpSecretAsync(new AdminTotpSecret
        {
            UserId = user.Id,
            SecretBase32 = secret,
            LastAcceptedStep = acceptedStep,
            EnrolledAt = now,
        }, context.RequestAborted).ConfigureAwait(false);
        await users.UpdateAsync(user with { TotpEnrolled = true }, context.RequestAborted).ConfigureAwait(false);

        var generatedCodes = recoveryCodes.GenerateCodes();
        await users.ReplaceRecoveryCodesAsync(user.Id, recoveryCodes.ToRows(user.Id, generatedCodes, now), context.RequestAborted)
            .ConfigureAwait(false);
        recoveryCodesCookie.Write(context, generatedCodes);
        await auditor.RecordAsync(AdminAuthEventKind.TotpEnrolled, user.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return Redirect("/account", status: "TOTP enrollment updated.");
    }

    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        RecoveryCodeService recoveryCodes,
        RecoveryCodesCookie recoveryCodesCookie,
        AdminAuthAuditor auditor)
    {
        await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!await HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/account", error: "Step-up verification is required before regenerating recovery codes.");
        }

        var now = DateTimeOffset.UtcNow;
        var generatedCodes = recoveryCodes.GenerateCodes();
        await users.ReplaceRecoveryCodesAsync(user.Id, recoveryCodes.ToRows(user.Id, generatedCodes, now), context.RequestAborted)
            .ConfigureAwait(false);
        recoveryCodesCookie.Write(context, generatedCodes);
        return Redirect("/account", status: "Recovery codes regenerated.");
    }

    private static async Task<IResult> BeginWebAuthnRegistrationAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        IWebAuthnService webAuthn,
        WebAuthnStateCookie stateCookie,
        AdminAuthAuditor auditor)
    {
        await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!await HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return JsonError("Step-up verification is required before enrolling a security key.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var existingCredentials = await users.ListWebAuthnCredentialsAsync(user.Id, context.RequestAborted).ConfigureAwait(false);
            var result = await webAuthn.BeginRegistrationAsync(
                user,
                existingCredentials.Select(c => c.CredentialId).ToList(),
                context.RequestAborted).ConfigureAwait(false);
            stateCookie.WriteRegistration(context, result.ServerState);
            return Results.Content(result.OptionsJson, "application/json");
        }
        catch (WebAuthnConfigurationException ex)
        {
            return JsonError(ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> CompleteWebAuthnRegistrationAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        IWebAuthnService webAuthn,
        WebAuthnStateCookie stateCookie,
        AdminAuthAuditor auditor)
    {
        await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!await HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return JsonError("Step-up verification is required before enrolling a security key.", StatusCodes.Status403Forbidden);
        }

        if (!stateCookie.TryReadRegistration(context, out var serverState))
        {
            return JsonError("Security-key enrollment expired.  Start enrollment again.", StatusCodes.Status400BadRequest);
        }

        stateCookie.ClearRegistration(context);
        WebAuthnRegistrationCompleteRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<WebAuthnRegistrationCompleteRequest>(
                context.Request.Body,
                JsonOptions,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return JsonError("Invalid security-key enrollment response.", StatusCodes.Status400BadRequest);
        }

        if (request?.Response is null)
        {
            return JsonError("Invalid security-key enrollment response.", StatusCodes.Status400BadRequest);
        }

        var result = await webAuthn.CompleteRegistrationAsync(
            serverState,
            request.Response.Value.GetRawText(),
            context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded || result.Credential is null)
        {
            return JsonError(UniformFailure, StatusCodes.Status400BadRequest);
        }

        var now = DateTimeOffset.UtcNow;
        var added = await users.AddWebAuthnCredentialAsync(new AdminWebAuthnCredential
        {
            Id = ViegardId.New(),
            UserId = user.Id,
            CredentialId = result.Credential.CredentialId,
            PublicKey = result.Credential.PublicKey,
            SignCount = result.Credential.SignCount,
            Aaguid = result.Credential.Aaguid,
            Transports = result.Credential.Transports,
            Name = NormalizeCredentialName(request.Name),
            CreatedAt = now,
            LastUsedAt = null,
        }, context.RequestAborted).ConfigureAwait(false);
        if (!added)
        {
            return JsonError("This security key is already enrolled.", StatusCodes.Status409Conflict);
        }

        await auditor.RecordAsync(AdminAuthEventKind.WebAuthnEnrolled, user.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return JsonRedirect(BuildRedirectPath("/account", status: "Security key enrolled."));
    }

    private static async Task<IResult> BeginWebAuthnAssertionAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IWebAuthnService webAuthn,
        WebAuthnStateCookie stateCookie,
        PendingTwoFactorCookie pendingCookie)
    {
        await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false);
        var user = await TryResolveWebAuthnUserAsync(context, users, pendingCookie).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var credentials = await users.ListWebAuthnCredentialsAsync(user.Id, context.RequestAborted).ConfigureAwait(false);
        if (credentials.Count == 0)
        {
            return Results.NoContent();
        }

        try
        {
            var result = await webAuthn.BeginAssertionAsync(
                credentials.Select(c => c.CredentialId).ToList(),
                context.RequestAborted).ConfigureAwait(false);
            stateCookie.WriteAssertion(context, result.ServerState);
            return Results.Content(result.OptionsJson, "application/json");
        }
        catch (WebAuthnConfigurationException)
        {
            return JsonError(UniformFailure, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> CompleteWebAuthnAssertionAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        PendingTwoFactorCookie pendingCookie,
        IWebAuthnService webAuthn,
        WebAuthnStateCookie stateCookie,
        IOptions<AdminAuthOptions> options,
        AdminAuthAuditor auditor)
    {
        await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false);
        var pendingFlow = pendingCookie.TryRead(context, out _);
        var assertionUser = await TryResolveWebAuthnUserAsync(context, users, pendingCookie).ConfigureAwait(false);
        if (assertionUser is null)
        {
            return JsonRedirect(BuildRedirectPath("/login", error: UniformFailure), StatusCodes.Status401Unauthorized);
        }

        if (assertionUser.LockedUntil is not null && assertionUser.LockedUntil > DateTimeOffset.UtcNow)
        {
            pendingCookie.Clear(context);
            await auditor.RecordAsync(
                AdminAuthEventKind.LoginFailed,
                assertionUser.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return JsonRedirect(BuildRedirectPath("/login", error: UniformFailure), StatusCodes.Status400BadRequest);
        }

        if (!stateCookie.TryReadAssertion(context, out var serverState))
        {
            await RecordWebAuthnFailureAsync(context, users, auditor, assertionUser).ConfigureAwait(false);
            return JsonRedirect(WebAuthnFailureRedirect(pendingFlow), StatusCodes.Status400BadRequest);
        }

        stateCookie.ClearAssertion(context);
        JsonDocument parsedDocument;
        try
        {
            parsedDocument = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await RecordWebAuthnFailureAsync(context, users, auditor, assertionUser).ConfigureAwait(false);
            return JsonRedirect(WebAuthnFailureRedirect(pendingFlow), StatusCodes.Status400BadRequest);
        }

        using var document = parsedDocument;
        if (!TryReadRawCredentialId(document.RootElement, out var rawCredentialId))
        {
            await RecordWebAuthnFailureAsync(context, users, auditor, assertionUser).ConfigureAwait(false);
            return JsonRedirect(WebAuthnFailureRedirect(pendingFlow), StatusCodes.Status400BadRequest);
        }

        var credential = await users.GetWebAuthnCredentialByCredentialIdAsync(rawCredentialId, context.RequestAborted)
            .ConfigureAwait(false);
        if (credential is null || credential.UserId != assertionUser.Id)
        {
            await RecordWebAuthnFailureAsync(context, users, auditor, assertionUser).ConfigureAwait(false);
            return JsonRedirect(WebAuthnFailureRedirect(pendingFlow), StatusCodes.Status400BadRequest);
        }

        var result = await webAuthn.CompleteAssertionAsync(
            serverState,
            document.RootElement.GetRawText(),
            new WebAuthnStoredCredential(
                credential.UserId,
                credential.CredentialId,
                credential.PublicKey,
                credential.SignCount),
            context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded || result.NewSignCount is null)
        {
            await RecordWebAuthnFailureAsync(context, users, auditor, assertionUser).ConfigureAwait(false);
            if (result.CloneWarning)
            {
                await auditor.RecordAsync(
                    AdminAuthEventKind.WebAuthnCloneWarning,
                    assertionUser.Username,
                    context,
                    enqueueForCorrelation: true,
                    cancellationToken: context.RequestAborted).ConfigureAwait(false);
            }

            return JsonRedirect(WebAuthnFailureRedirect(pendingFlow), StatusCodes.Status400BadRequest);
        }

        var now = DateTimeOffset.UtcNow;
        await users.UpdateWebAuthnCredentialUsageAsync(credential.Id, result.NewSignCount.Value, now, context.RequestAborted)
            .ConfigureAwait(false);

        if (pendingFlow)
        {
            await CompleteSuccessfulPendingSecondFactorAsync(
                context,
                users,
                sessions,
                pendingCookie,
                assertionUser,
                options.Value,
                auditor,
                usedRecoveryCode: false,
                redirectPath: "/account").ConfigureAwait(false);
            return JsonRedirect("/account");
        }

        if (!TryGetCurrentSessionId(context.User, out var sessionId))
        {
            return JsonRedirect(BuildRedirectPath("/login", error: UniformFailure), StatusCodes.Status401Unauthorized);
        }

        await sessions.StampStepUpAsync(sessionId, now, context.RequestAborted).ConfigureAwait(false);
        await auditor.RecordAsync(AdminAuthEventKind.StepUpSucceeded, assertionUser.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return JsonRedirect(BuildRedirectPath("/account", status: "Step-up verification complete."));
    }

    private static async Task<IResult> DeleteWebAuthnCredentialAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor auditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!await HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/account", error: "Step-up verification is required before removing a security key.");
        }

        if (!Guid.TryParse(form["credentialId"].ToString(), out var credentialId))
        {
            return Redirect("/account", error: "Security key not found.");
        }

        var deleted = await users.DeleteWebAuthnCredentialAsync(user.Id, credentialId, context.RequestAborted).ConfigureAwait(false);
        if (!deleted)
        {
            return Redirect("/account", error: "Security key not found.");
        }

        await auditor.RecordAsync(AdminAuthEventKind.WebAuthnRemoved, user.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return Redirect("/account", status: "Security key removed.");
    }

    private static async Task<IResult> StepUpAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        TotpService totp,
        AdminAuthAuditor auditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null || !TryGetCurrentSessionId(context.User, out var sessionId))
        {
            return Results.Redirect("/login");
        }

        var secret = await users.GetTotpSecretAsync(user.Id, context.RequestAborted).ConfigureAwait(false);
        var code = form["code"].ToString();
        var valid = secret is not null
            && totp.VerifyCode(secret.SecretBase32, code, secret.LastAcceptedStep, out var acceptedStep)
            && await users.TrySetTotpLastAcceptedStepAsync(user.Id, acceptedStep, context.RequestAborted).ConfigureAwait(false);
        if (!valid)
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/account", error: UniformFailure);
        }

        await sessions.StampStepUpAsync(sessionId, DateTimeOffset.UtcNow, context.RequestAborted).ConfigureAwait(false);
        await auditor.RecordAsync(AdminAuthEventKind.StepUpSucceeded, user.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return Redirect("/account", status: "Step-up verification complete.");
    }

    private static async Task<IResult> RevokeSessionsAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor auditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (!TryGetCurrentUserId(context.User, out var userId) || !TryGetCurrentSessionId(context.User, out var currentSessionId))
        {
            return Results.Redirect("/login");
        }

        var all = string.Equals(form["scope"].ToString(), "all", StringComparison.OrdinalIgnoreCase);
        if (all)
        {
            await sessions.RevokeForUserAsync(userId, now, exceptSessionId: null, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        }
        else if (Guid.TryParse(form["sessionId"].ToString(), out var sessionId))
        {
            // Ownership check: a session id posted in the form must belong
            // to the calling user before it can be revoked.
            var target = await sessions.GetAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            if (target is null || target.UserId != userId)
            {
                return Redirect("/account", error: "Session not found.");
            }

            await sessions.RevokeAsync(sessionId, now, context.RequestAborted).ConfigureAwait(false);
            if (sessionId == currentSessionId)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            }
        }

        var user = await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false);
        await auditor.RecordAsync(AdminAuthEventKind.SessionRevoked, user?.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return all ? Results.Redirect("/login?status=Signed%20out%20everywhere.") : Redirect("/account", status: "Session revoked.");
    }

    private static async Task<IResult> CompleteSuccessfulPendingSecondFactorAsync(
        HttpContext context,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        PendingTwoFactorCookie pendingCookie,
        AdminUser user,
        AdminAuthOptions options,
        AdminAuthAuditor auditor,
        bool usedRecoveryCode,
        string redirectPath)
    {
        if (user.FailedLoginCount > 0 || user.LockedUntil is not null)
        {
            user = user with { FailedLoginCount = 0, LockedUntil = null };
            await users.UpdateAsync(user, context.RequestAborted).ConfigureAwait(false);
        }

        if (usedRecoveryCode)
        {
            await auditor.RecordAsync(AdminAuthEventKind.RecoveryCodeUsed, user.Username, context, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
        }

        await SignInAsync(context, sessions, pendingCookie, user, options).ConfigureAwait(false);
        await auditor.RecordAsync(AdminAuthEventKind.LoginSucceeded, user.Username, context, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        return Results.Redirect(redirectPath);
    }

    private static async Task RecordWebAuthnFailureAsync(
        HttpContext context,
        IAdminUserStore users,
        AdminAuthAuditor auditor,
        AdminUser user)
    {
        var failedCount = user.FailedLoginCount + 1;
        var lockedUntil = failedCount >= 10
            ? DateTimeOffset.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, failedCount - 10)))
            : (DateTimeOffset?)null;
        await users.UpdateAsync(user with
        {
            FailedLoginCount = failedCount,
            LockedUntil = lockedUntil,
        }, context.RequestAborted).ConfigureAwait(false);

        await auditor.RecordAsync(
            AdminAuthEventKind.WebAuthnFailed,
            user.Username,
            context,
            enqueueForCorrelation: true,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
        if (lockedUntil is not null)
        {
            await auditor.RecordAsync(
                AdminAuthEventKind.LockoutTriggered,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async ValueTask<AdminUser?> TryResolveWebAuthnUserAsync(
        HttpContext context,
        IAdminUserStore users,
        PendingTwoFactorCookie pendingCookie)
    {
        if (pendingCookie.TryRead(context, out var pendingUserId))
        {
            return await users.GetByIdAsync(pendingUserId, context.RequestAborted).ConfigureAwait(false);
        }

        return await GetCurrentUserAsync(context, users).ConfigureAwait(false);
    }

    private static bool TryReadRawCredentialId(JsonElement root, out byte[] credentialId)
    {
        credentialId = [];
        if (!root.TryGetProperty("rawId", out var rawIdProperty)
            || rawIdProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return WebAuthnBase64Url.TryDecode(rawIdProperty.GetString(), out credentialId);
    }

    private static string NormalizeCredentialName(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? "Security key" : name.Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static IResult JsonRedirect(string redirect, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(new { redirect }, JsonOptions, statusCode: statusCode);

    private static IResult JsonError(string error, int statusCode) =>
        Results.Json(new { error }, JsonOptions, statusCode: statusCode);

    private static string WebAuthnFailureRedirect(bool pendingFlow) =>
        pendingFlow
            ? BuildRedirectPath("/login/2fa", error: UniformFailure)
            : BuildRedirectPath("/account", error: UniformFailure);

    private static async Task SignInAsync(
        HttpContext context,
        IAdminSessionStore sessions,
        PendingTwoFactorCookie pendingCookie,
        AdminUser user,
        AdminAuthOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AdminSession
        {
            Id = ViegardId.New(),
            UserId = user.Id,
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiresAt = now.Add(options.AbsoluteLifetime),
            IdleExpiresAt = now.Add(options.IdleTimeout),
            // Normalized so the session list shows 192.168.0.x rather than
            // the dual-stack socket's ::ffff:192.168.0.x mapped form.
            Ip = context.Connection.RemoteIpAddress is { } remote
                ? AdminIpBinding.Normalize(remote).ToString()
                : string.Empty,
            IpBindingMode = AdminIpBindingModes.Normalize(options.IpBindingMode),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            RevokedAt = null,
            StepUpAt = now,
        };
        await sessions.CreateAsync(session, context.RequestAborted).ConfigureAwait(false);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(AdminCookieNames.SessionIdClaim, session.Id.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = session.AbsoluteExpiresAt,
            }).ConfigureAwait(false);
        pendingCookie.Clear(context);
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        return await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery) =>
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);

    private static async ValueTask<AdminUser?> GetCurrentUserAsync(HttpContext context, IAdminUserStore users)
    {
        if (!TryGetCurrentUserId(context.User, out var userId))
        {
            return null;
        }

        return await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool TryGetCurrentUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool TryGetCurrentSessionId(ClaimsPrincipal principal, out Guid sessionId) =>
        Guid.TryParse(principal.FindFirstValue(AdminCookieNames.SessionIdClaim), out sessionId);

    private static async ValueTask<bool> HasRecentStepUpAsync(HttpContext context, IAdminSessionStore sessions)
    {
        if (!TryGetCurrentSessionId(context.User, out var sessionId))
        {
            return false;
        }

        var session = await sessions.GetAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        if (session?.StepUpAt is null)
        {
            return false;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<AdminAuthOptions>>().Value;
        return session.StepUpAt.Value.Add(options.StepUpValidity) >= DateTimeOffset.UtcNow;
    }

    private static IResult Redirect(string path, string? status = null, string? error = null) =>
        Results.Redirect(BuildRedirectPath(path, status, error));

    private static string BuildRedirectPath(string path, string? status = null, string? error = null)
    {
        var query = status is not null
            ? $"status={Uri.EscapeDataString(status)}"
            : error is not null
                ? $"error={Uri.EscapeDataString(error)}"
                : string.Empty;
        return string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
    }

    private sealed record WebAuthnRegistrationCompleteRequest(string? Name, JsonElement? Response);
}
