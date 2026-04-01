using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Auth;

public class AuthService
{
    private static readonly TimeSpan VerificationCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PasswordResetCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EmailCooldown = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly JwtHelper _jwt;
    private readonly IEmailService _email;
    private readonly AuditLogService _audit;

    public AuthService(AppDbContext db, JwtHelper jwt, IEmailService email, AuditLogService audit)
    {
        _db = db;
        _jwt = jwt;
        _email = email;
        _audit = audit;
    }

    public async Task<(MessageResponse? Response, int StatusCode, string? Error)> RegisterAsync(
        RegisterRequest request,
        string? ip = null
    )
    {
        // Validate email
        if (string.IsNullOrWhiteSpace(request.Email) || !IsValidEmail(request.Email))
            return (null, 400, "A valid email address is required.");

        // Validate display name
        if (
            request.DisplayName == null
            || request.DisplayName.Length < 1
            || request.DisplayName.Length > 24
        )
            return (null, 400, "Display name must be 1-24 characters.");

        // Validate password
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return (null, 400, "Password must be at least 8 characters.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var successMessage = "Check your email for a verification code.";

        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (existing != null)
        {
            // Don't reveal that the email is taken — silently notify the owner
            try
            {
                await _email.SendAlreadyRegisteredEmailAsync(existing.Email);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to send already-registered email: {ex.Message}");
            }

            return (new MessageResponse(successMessage), 200, null);
        }

        // Generate 6-digit verification code
        var code = GenerateVerificationCode();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            DisplayName = request.DisplayName,
            PasswordHash = PasswordHasher.Hash(request.Password),
            CreatedAt = DateTime.UtcNow,
            VerificationCode = code,
            VerificationCodeExpiresAt = DateTime.UtcNow.Add(VerificationCodeLifetime),
            LastVerificationEmailAt = DateTime.UtcNow,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.Register, user.Id, user.Email, ip);

        try
        {
            await _email.SendVerificationCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send verification email: {ex.Message}");
        }

        return (new MessageResponse(successMessage), 200, null);
    }

    public async Task<(MeResponse? Response, int StatusCode, string? Error)> GetMeAsync(Guid userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return (null, 401, "User not found.");

        return (new MeResponse(user.Email, user.DisplayName), 200, null);
    }

    public async Task<(AuthResponse? Response, int StatusCode, string? Error)> LoginAsync(
        LoginRequest request,
        string? ip = null
    )
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return (null, 400, "Email and password are required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (
            user == null
            || !PasswordHasher.Verify(request.Password, user.PasswordHash)
            || !user.IsEmailVerified
        )
        {
            await _audit.LogAsync(AuditEvent.LoginFailed, user?.Id, normalizedEmail, ip);
            return (null, 401, "Invalid email or password.");
        }

        if (user.IsLocked)
        {
            await _audit.LogAsync(
                AuditEvent.LoginFailed,
                user.Id,
                normalizedEmail,
                ip,
                "Account locked"
            );
            return (null, 403, "Account is locked. Please contact support on Discord.");
        }

        await _audit.LogAsync(AuditEvent.Login, user.Id, user.Email, ip);
        var token = _jwt.GenerateToken(user);
        return (new AuthResponse(token, user.DisplayName), 200, null);
    }

    public async Task<(
        DisplayNameResponse? Response,
        int StatusCode,
        string? Error
    )> UpdateDisplayNameAsync(Guid userId, UpdateDisplayNameRequest request, string? ip = null)
    {
        if (
            request.DisplayName == null
            || request.DisplayName.Length < 1
            || request.DisplayName.Length > 24
        )
            return (null, 400, "Display name must be 1-24 characters.");

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return (null, 401, "User not found.");

        var oldName = user.DisplayName;
        user.DisplayName = request.DisplayName;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            AuditEvent.UpdateDisplayName,
            user.Id,
            user.Email,
            ip,
            $"'{oldName}' -> '{user.DisplayName}'"
        );
        return (new DisplayNameResponse(user.DisplayName), 200, null);
    }

    public async Task<(
        MessageResponse? Response,
        int StatusCode,
        string? Error
    )> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? ip = null)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            return (null, 400, "Current password is required.");

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return (null, 400, "New password must be at least 8 characters.");

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return (null, 401, "User not found.");

        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return (null, 401, "Incorrect password.");

        if (PasswordHasher.Verify(request.NewPassword, user.PasswordHash))
            return (null, 400, "New password must be different from current password.");

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString();
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.ChangePassword, user.Id, user.Email, ip);
        return (new MessageResponse("Password changed successfully."), 200, null);
    }

    public async Task<(AuthResponse? Response, int StatusCode, string? Error)> VerifyCodeAsync(
        VerifyCodeRequest request,
        string? ip = null
    )
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
            return (null, 400, "Email and verification code are required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (user == null || user.VerificationCode == null)
            return (null, 400, "Invalid or expired verification code.");

        if (user.VerificationCodeExpiresAt < DateTime.UtcNow)
        {
            user.VerificationCode = null;
            user.VerificationCodeExpiresAt = null;
            await _db.SaveChangesAsync();
            return (null, 400, "Verification code has expired. Please request a new one.");
        }

        if (user.VerificationCode != request.Code.Trim())
            return (null, 400, "Invalid or expired verification code.");

        user.EmailVerifiedAt = DateTime.UtcNow;
        user.VerificationCode = null;
        user.VerificationCodeExpiresAt = null;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.VerifyEmail, user.Id, user.Email, ip);
        var jwt = _jwt.GenerateToken(user);
        return (new AuthResponse(jwt, user.DisplayName), 200, null);
    }

    public async Task<(
        MessageResponse? Response,
        int StatusCode,
        string? Error
    )> ResendVerificationAsync(ResendVerificationRequest request, string? ip = null)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return (null, 400, "Email is required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        // Always return success to prevent email enumeration
        var successMessage =
            "If an account with that email exists, a verification email has been sent.";

        if (user == null || user.IsEmailVerified)
            return (new MessageResponse(successMessage), 200, null);

        // Rate limit
        if (
            user.LastVerificationEmailAt.HasValue
            && DateTime.UtcNow - user.LastVerificationEmailAt.Value < EmailCooldown
        )
            return (null, 429, "Please wait a few minutes before requesting another code.");

        var code = GenerateVerificationCode();
        user.VerificationCode = code;
        user.VerificationCodeExpiresAt = DateTime.UtcNow.Add(VerificationCodeLifetime);
        user.LastVerificationEmailAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.ResendVerification, user.Id, user.Email, ip);

        try
        {
            await _email.SendVerificationCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send verification email: {ex.Message}");
        }

        return (new MessageResponse(successMessage), 200, null);
    }

    public async Task<(
        MessageResponse? Response,
        int StatusCode,
        string? Error
    )> ForgotPasswordAsync(ForgotPasswordRequest request, string? ip = null)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return (null, 400, "Email is required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        // Always return same message to prevent email enumeration
        var successMessage =
            "If an account with that email exists, a password reset code has been sent.";

        if (user == null)
            return (new MessageResponse(successMessage), 200, null);

        // Rate limit
        if (
            user.LastPasswordResetEmailAt.HasValue
            && DateTime.UtcNow - user.LastPasswordResetEmailAt.Value < EmailCooldown
        )
            return (null, 429, "Please wait a few minutes before requesting another reset.");

        var code = GenerateVerificationCode();
        user.PasswordResetCode = code;
        user.PasswordResetCodeExpiresAt = DateTime.UtcNow.Add(PasswordResetCodeLifetime);
        user.LastPasswordResetEmailAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.ForgotPassword, user.Id, user.Email, ip);

        try
        {
            await _email.SendPasswordResetCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send password reset email: {ex.Message}");
        }

        return (new MessageResponse(successMessage), 200, null);
    }

    public async Task<(
        MessageResponse? Response,
        int StatusCode,
        string? Error
    )> ResetPasswordAsync(ResetPasswordRequest request, string? ip = null)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
            return (null, 400, "Email and reset code are required.");

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return (null, 400, "Password must be at least 8 characters.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (user == null || user.PasswordResetCode == null)
            return (null, 400, "Invalid or expired reset code.");

        if (user.PasswordResetCodeExpiresAt < DateTime.UtcNow)
        {
            user.PasswordResetCode = null;
            user.PasswordResetCodeExpiresAt = null;
            await _db.SaveChangesAsync();
            return (null, 400, "Reset code has expired. Please request a new one.");
        }

        if (user.PasswordResetCode != request.Code.Trim())
            return (null, 400, "Invalid or expired reset code.");

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.PasswordResetCode = null;
        user.PasswordResetCodeExpiresAt = null;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.ResetPassword, user.Id, user.Email, ip);
        return (new MessageResponse("Password has been reset. You can now log in."), 200, null);
    }

    public async Task<(MessageResponse? Response, int StatusCode, string? Error)> ChangeEmailAsync(
        Guid userId,
        ChangeEmailRequest request,
        string? ip = null
    )
    {
        if (string.IsNullOrWhiteSpace(request.NewEmail) || !IsValidEmail(request.NewEmail))
            return (null, 400, "A valid email address is required.");

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            return (null, 400, "Current password is required.");

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return (null, 401, "User not found.");

        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return (null, 401, "Incorrect password.");

        var normalizedNewEmail = request.NewEmail.Trim().ToLowerInvariant();

        if (normalizedNewEmail == user.Email)
            return (null, 400, "New email is the same as current email.");

        if (await _db.Users.AnyAsync(u => u.Email == normalizedNewEmail))
            return (null, 409, "An account with this email already exists.");

        // Generate confirmation code for the new email
        var code = GenerateVerificationCode();
        user.PendingEmail = normalizedNewEmail;
        user.PendingEmailCode = code;
        user.PendingEmailCodeExpiresAt = DateTime.UtcNow.Add(VerificationCodeLifetime);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            AuditEvent.ChangeEmail,
            user.Id,
            user.Email,
            ip,
            $"Requested change to {normalizedNewEmail}"
        );

        // Send verification code to new email
        try
        {
            await _email.SendEmailChangeCodeAsync(normalizedNewEmail, code);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send email change verification: {ex.Message}");
        }

        // Notify old email
        try
        {
            await _email.SendEmailChangeNotificationAsync(user.Email, normalizedNewEmail);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send email change notification: {ex.Message}");
        }

        return (
            new MessageResponse("A confirmation code has been sent to your new email address."),
            200,
            null
        );
    }

    public async Task<(
        MessageResponse? Response,
        int StatusCode,
        string? Error
    )> ConfirmEmailChangeAsync(Guid userId, ConfirmEmailChangeRequest request, string? ip = null)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
            return (null, 400, "Email and confirmation code are required.");

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return (null, 401, "User not found.");

        if (user.PendingEmailCode == null || user.PendingEmail == null)
            return (null, 400, "No pending email change.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (normalizedEmail != user.PendingEmail)
            return (null, 400, "Invalid or expired confirmation code.");

        if (user.PendingEmailCodeExpiresAt < DateTime.UtcNow)
        {
            user.PendingEmail = null;
            user.PendingEmailCode = null;
            user.PendingEmailCodeExpiresAt = null;
            await _db.SaveChangesAsync();
            return (null, 400, "Confirmation code has expired. Please request a new email change.");
        }

        if (user.PendingEmailCode != request.Code.Trim())
            return (null, 400, "Invalid or expired confirmation code.");

        // Check the new email hasn't been taken since the change was requested
        if (await _db.Users.AnyAsync(u => u.Email == user.PendingEmail && u.Id != user.Id))
        {
            user.PendingEmail = null;
            user.PendingEmailCode = null;
            user.PendingEmailCodeExpiresAt = null;
            await _db.SaveChangesAsync();
            return (null, 409, "An account with this email already exists.");
        }

        var oldEmail = user.Email;
        user.Email = user.PendingEmail;
        user.EmailVerifiedAt = DateTime.UtcNow;
        user.PendingEmail = null;
        user.PendingEmailCode = null;
        user.PendingEmailCodeExpiresAt = null;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            AuditEvent.ConfirmEmailChange,
            user.Id,
            user.Email,
            ip,
            $"Changed from {oldEmail}"
        );
        return (new MessageResponse("Email changed successfully."), 200, null);
    }

    public async Task<(MessageResponse? Response, int StatusCode, string? Error)> LockAccountAsync(
        LockAccountRequest request,
        string? ip = null
    )
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return (null, 400, "Email is required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (user == null)
            return (null, 404, "User not found.");

        // Lock the account
        user.LockedAt = DateTime.UtcNow;

        // Revert pending email change if any
        user.PendingEmail = null;
        user.PendingEmailCode = null;
        user.PendingEmailCodeExpiresAt = null;

        // Clear all codes
        user.VerificationCode = null;
        user.VerificationCodeExpiresAt = null;
        user.PasswordResetCode = null;
        user.PasswordResetCodeExpiresAt = null;

        // Bump security stamp to invalidate all existing JWTs
        user.SecurityStamp = Guid.NewGuid().ToString();

        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.LockAccount, user.Id, user.Email, ip);
        return (
            new MessageResponse(
                $"Account '{user.Email}' locked. All sessions invalidated. Use unlock-account to restore access."
            ),
            200,
            null
        );
    }

    public async Task<(
        MessageResponse? Response,
        int StatusCode,
        string? Error
    )> UnlockAccountAsync(LockAccountRequest request, string? ip = null)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return (null, 400, "Email is required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (user == null)
            return (null, 404, "User not found.");

        if (!user.IsLocked)
            return (null, 400, "Account is not locked.");

        user.LockedAt = null;

        // Generate a password reset code so the user can set a new password
        var code = GenerateVerificationCode();
        user.PasswordResetCode = code;
        user.PasswordResetCodeExpiresAt = DateTime.UtcNow.Add(PasswordResetCodeLifetime);

        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.UnlockAccount, user.Id, user.Email, ip);

        // Send password reset email
        try
        {
            await _email.SendPasswordResetCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send password reset email: {ex.Message}");
        }

        return (
            new MessageResponse($"Account '{user.Email}' unlocked. Password reset email sent."),
            200,
            null
        );
    }

    private static string GenerateVerificationCode()
    {
        return Random.Shared.Next(100000, 999999).ToString();
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
            return false;

        // Simple validation: must have exactly one @ with content on both sides
        var parts = email.Trim().Split('@');
        return parts.Length == 2
            && parts[0].Length > 0
            && parts[1].Length > 0
            && parts[1].Contains('.');
    }
}
