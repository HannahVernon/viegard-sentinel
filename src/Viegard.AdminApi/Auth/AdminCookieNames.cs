namespace Viegard.AdminApi.Auth;

public static class AdminCookieNames
{
    public const string Session = "viegard.admin.session";
    public const string PendingTwoFactor = "viegard.admin.pending_2fa";
    public const string RecoveryCodes = "viegard.admin.recovery_codes";
    public const string NewAppPassword = "viegard.admin.new_app_password";
    public const string SatelliteRoleSecret = "viegard.admin.satellite_role_secret";
    public const string WebAuthnRegistrationState = "viegard.admin.webauthn_registration";
    public const string WebAuthnAssertionState = "viegard.admin.webauthn_assertion";
    public const string SessionIdClaim = "viegard.session_id";
}
