namespace ArrowThing.Server.Models;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; }

    // Email verification
    public DateTime? EmailVerifiedAt { get; set; }
    public string? VerificationCode { get; set; }
    public DateTime? VerificationCodeExpiresAt { get; set; }
    public DateTime? LastVerificationEmailAt { get; set; }

    // Email change (pending until verified)
    public string? PendingEmail { get; set; }
    public string? PendingEmailCode { get; set; }
    public DateTime? PendingEmailCodeExpiresAt { get; set; }

    // Password reset
    public string? PasswordResetCode { get; set; }
    public DateTime? PasswordResetCodeExpiresAt { get; set; }
    public DateTime? LastPasswordResetEmailAt { get; set; }

    // Failed login tracking
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }

    // Account lock (admin-initiated)
    public DateTime? LockedAt { get; set; }

    // New-device verification: email OTP challenge when logging in from an
    // unrecognized device id. The pending OTP is tied to the specific device
    // id that requested it so concurrent verifications from different devices
    // don't clobber each other.
    public string? DeviceOtpCode { get; set; }
    public DateTime? DeviceOtpCodeExpiresAt { get; set; }
    public string? DeviceOtpPendingDeviceIdHash { get; set; }
    public DateTime? LastDeviceOtpEmailAt { get; set; }

    // Anti-cheat flagging
    public bool Flagged { get; set; }
    public string? FlagReason { get; set; }
    public DateTime? FlaggedAt { get; set; }
    public int? FlagTriggerSeed { get; set; }
    public int? FlagTriggerBoardWidth { get; set; }
    public int? FlagTriggerBoardHeight { get; set; }
    public string? FlagTriggerGameId { get; set; }

    // Co-op preferences
    public string? CoopColor { get; set; }

    public bool IsLocked => LockedAt.HasValue;
    public bool IsEmailVerified => EmailVerifiedAt.HasValue;
}
