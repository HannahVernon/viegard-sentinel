namespace Viegard.Application.Auth;

public static class PasswordPolicy
{
    public const int MinimumLength = 20;

    public static bool IsValid(string? password, out string error)
    {
        if (password is null || password.Length < MinimumLength)
        {
            error = $"Passwords must be at least {MinimumLength} characters long.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
