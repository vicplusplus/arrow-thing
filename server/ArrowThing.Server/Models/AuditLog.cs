namespace ArrowThing.Server.Models;

public class AuditLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Event { get; set; } = "";
    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public string? IpAddress { get; set; }
    public string? Detail { get; set; }
}

public static class AuditEvent
{
    public const string Register = "register";
    public const string Login = "login";
    public const string LoginFailed = "login_failed";
    public const string VerifyEmail = "verify_email";
    public const string ResendVerification = "resend_verification";
    public const string ForgotPassword = "forgot_password";
    public const string ResetPassword = "reset_password";
    public const string ChangePassword = "change_password";
    public const string ChangeEmail = "change_email";
    public const string ConfirmEmailChange = "confirm_email_change";
    public const string LockAccount = "lock_account";
    public const string UnlockAccount = "unlock_account";
    public const string UpdateDisplayName = "update_display_name";
    public const string SessionInvalidated = "session_invalidated";
    public const string DeviceOtpRequested = "device_otp_requested";
    public const string DeviceVerified = "device_verified";
    public const string UpdateCoopColor = "update_coop_color";
}
