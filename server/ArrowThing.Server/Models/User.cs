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

    // Anti-cheat flagging
    public bool Flagged { get; set; }
    public string? FlagReason { get; set; }

    public bool IsLocked => LockedAt.HasValue;
    public bool IsEmailVerified => EmailVerifiedAt.HasValue;
}
