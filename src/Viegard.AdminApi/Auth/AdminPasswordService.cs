using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Viegard.Application.Auth;
using Viegard.Domain.Admin;

namespace Viegard.AdminApi.Auth;

public sealed class AdminPasswordService
{
    private readonly PasswordHasher<AdminUser> _hasher = new(Options.Create(new PasswordHasherOptions
    {
        // ASP.NET Core Identity V3 hashes use PBKDF2-HMAC-SHA512 in .NET 10.
        // OWASP's 2024 guidance lists 210,000 iterations for PBKDF2-HMAC-SHA512.
        IterationCount = 210_000,
    }));

    public bool ValidateNewPassword(string? password, out string error) =>
        PasswordPolicy.IsValid(password, out error);

    public string HashPassword(AdminUser user, string password) =>
        _hasher.HashPassword(user, password);

    public PasswordVerificationResult Verify(AdminUser user, string password) =>
        _hasher.VerifyHashedPassword(user, user.PasswordHash, password);

    public bool VerifyPassword(AdminUser user, string password) =>
        Verify(user, password) != PasswordVerificationResult.Failed;

    /// <summary>
    /// Hash of a throwaway password, verified against when the username is
    /// unknown or the account is locked so those paths cost the same time
    /// as a real verification (no username-existence timing oracle).
    /// </summary>
    private readonly string _dummyHash = new PasswordHasher<AdminUser>(Options.Create(new PasswordHasherOptions
    {
        IterationCount = 210_000,
    })).HashPassword(new AdminUser
    {
        Id = Guid.Empty,
        Username = string.Empty,
        PasswordHash = string.Empty,
        PasswordChangedAt = DateTimeOffset.MinValue,
        FailedLoginCount = 0,
        LockedUntil = null,
        MustChangePassword = false,
        TotpEnrolled = false,
        CreatedAt = DateTimeOffset.MinValue,
    }, Guid.NewGuid().ToString("N"));

    public void BurnTimingEquivalent(string password)
    {
        var decoy = new AdminUser
        {
            Id = Guid.Empty,
            Username = string.Empty,
            PasswordHash = _dummyHash,
            PasswordChangedAt = DateTimeOffset.MinValue,
            FailedLoginCount = 0,
            LockedUntil = null,
            MustChangePassword = false,
            TotpEnrolled = false,
            CreatedAt = DateTimeOffset.MinValue,
        };
        _ = _hasher.VerifyHashedPassword(decoy, _dummyHash, password);
    }
}
