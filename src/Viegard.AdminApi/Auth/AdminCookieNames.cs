namespace Viegard.AdminApi.Auth;

public static class AdminCookieNames
{
    public const string Session = "viegard.admin.session";
    public const string PendingTwoFactor = "viegard.admin.pending_2fa";
    public const string RecoveryCodes = "viegard.admin.recovery_codes";
    public const string SessionIdClaim = "viegard.session_id";
}
