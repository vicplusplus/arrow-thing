using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArrowThing.Server.Auth;

public class AuthService
{
    private static readonly TimeSpan VerificationCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PasswordResetCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeviceOtpCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeviceMaxAge = TimeSpan.FromDays(90);
    private static readonly TimeSpan EmailCooldown = TimeSpan.FromMinutes(5);
    private const int MaxFailedLoginAttempts = 5;
    private const int DeviceCountWarnThreshold = 10;
    private static readonly TimeSpan LoginLockoutDuration = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly JwtHelper _jwt;
    private readonly IEmailService _email;
    private readonly AuditLogService _audit;
    private readonly RefreshTokenService _refreshTokens;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        JwtHelper jwt,
        IEmailService email,
        AuditLogService audit,
        RefreshTokenService refreshTokens,
        ILogger<AuthService> logger
    )
    {
        _db = db;
        _jwt = jwt;
        _email = email;
        _audit = audit;
        _refreshTokens = refreshTokens;
        _logger = logger;
    }

    /// <summary>
    /// Mints an access JWT + refresh token pair. The refresh token row is
    /// added to the EF tracker but not saved here — caller is responsible
    /// for the next SaveChanges.
    /// </summary>
    private AuthResponse IssueTokenPair(User user, string? userAgent)
    {
        var jwt = _jwt.GenerateToken(user);
        var refresh = _refreshTokens.Issue(user.Id, userAgent);
        return new AuthResponse(
            Token: jwt,
            DisplayName: user.DisplayName,
            RefreshToken: refresh.Token,
            ExpiresIn: (int)JwtHelper.AccessTokenLifetime.TotalSeconds,
            RefreshExpiresIn: (int)RefreshTokenService.Lifetime.TotalSeconds
        );
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
                _logger.LogError(ex, "Failed to send already-registered email");
            }

            return (new MessageResponse(successMessage), 200, null);
        }

        // Generate 6-digit verification code
        var code = PasswordHasher.GenerateSecureCode();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            DisplayName = request.DisplayName,
            PasswordHash = PasswordHasher.Hash(request.Password),
            CreatedAt = DateTime.UtcNow,
            VerificationCode = PasswordHasher.HashOtp(code),
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
            _logger.LogError(ex, "Failed to send verification email");
            // Discard the hashed code so the 5-minute cooldown doesn't leave
            // the user unable to retry, and so a leaked-but-undelivered code
            // can't be used later.
            user.VerificationCode = null;
            user.VerificationCodeExpiresAt = null;
            user.LastVerificationEmailAt = null;
            await _db.SaveChangesAsync();
            return (null, 503, "Failed to send email. Please try again.");
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

    public async Task<(object? Response, int StatusCode, string? Error)> LoginAsync(
        LoginRequest request,
        string? ip = null,
        string? deviceId = null,
        string? userAgent = null
    )
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return (null, 400, "Email and password are required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        // Check temporary lockout from failed attempts
        if (user != null && user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
            return (null, 429, "Too many failed attempts. Please try again later.");

        if (
            user == null
            || !PasswordHasher.Verify(request.Password, user.PasswordHash)
            || !user.IsEmailVerified
        )
        {
            // Track failed attempts for existing users
            if (user != null)
            {
                // Reset counter if lockout has expired
                if (user.LockoutEnd.HasValue && user.LockoutEnd.Value <= DateTime.UtcNow)
                    user.FailedLoginAttempts = 0;

                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
                    user.LockoutEnd = DateTime.UtcNow.Add(LoginLockoutDuration);

                await _db.SaveChangesAsync();
            }

            await _audit.LogAsync(AuditEvent.LoginFailed, user?.Id, normalizedEmail, ip);
            return (null, 401, "Invalid email or password.");
        }

        if (user.IsLocked)
            return (null, 403, "Account is locked. Please contact support on Discord.");

        // Reset failed attempts on successful login
        if (user.FailedLoginAttempts > 0)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
            await _db.SaveChangesAsync();
        }

        // New-device OTP gate. If the client hasn't identified itself with a
        // known device id, send an email code and respond with a pending
        // challenge instead of issuing a JWT.
        var deviceResult = await TryMatchDeviceAsync(user, deviceId);
        if (!deviceResult.Matched)
        {
            var sendResult = await EnsureDeviceOtpSentAsync(user, deviceId, ip);
            if (sendResult.StatusCode != 200)
                return (null, sendResult.StatusCode, sendResult.Error);
            return (new DeviceOtpRequiredResponse(), 200, null);
        }

        // Known device — bump LastSeenAt so it stays within the 90-day window.
        var now = DateTime.UtcNow;
        await _db
            .UserDevices.Where(d => d.Id == deviceResult.MatchedDeviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now));

        await _audit.LogAsync(AuditEvent.Login, user.Id, user.Email, ip);

        var response = IssueTokenPair(user, userAgent);
        await _db.SaveChangesAsync();
        return (response, 200, null);
    }

    /// <summary>
    /// Completes the new-device OTP challenge. Requires the same password as
    /// the original login (defense in depth — the email code alone shouldn't
    /// grant access) and must come from the exact device id that requested
    /// the OTP. On success, records the device as known and issues a JWT.
    /// </summary>
    public async Task<(AuthResponse? Response, int StatusCode, string? Error)> VerifyDeviceAsync(
        VerifyDeviceRequest request,
        string? ip = null,
        string? deviceId = null,
        string? userAgent = null
    )
    {
        if (
            string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password)
            || string.IsNullOrWhiteSpace(request.Code)
        )
            return (null, 400, "Email, password, and code are required.");

        if (string.IsNullOrWhiteSpace(deviceId))
            return (null, 400, "Missing device id.");

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
            return (null, 403, "Account is locked. Please contact support on Discord.");

        if (user.DeviceOtpCode == null || user.DeviceOtpPendingDeviceIdHash == null)
            return (null, 400, "No pending device verification.");

        if (user.DeviceOtpCodeExpiresAt < DateTime.UtcNow)
        {
            user.DeviceOtpCode = null;
            user.DeviceOtpCodeExpiresAt = null;
            user.DeviceOtpPendingDeviceIdHash = null;
            await _db.SaveChangesAsync();
            return (null, 400, "Verification code has expired. Please log in again.");
        }

        // The pending OTP is bound to the specific device id that requested
        // it. A different device can't redeem this code — it has to request
        // its own.
        if (!PasswordHasher.VerifyOtp(deviceId!, user.DeviceOtpPendingDeviceIdHash))
            return (null, 400, "Invalid or expired verification code.");

        if (!PasswordHasher.VerifyOtp(request.Code.Trim(), user.DeviceOtpCode))
            return (null, 400, "Invalid or expired verification code.");

        await EnsureDeviceTrustedAsync(user, deviceId!, userAgent);

        user.DeviceOtpCode = null;
        user.DeviceOtpCodeExpiresAt = null;
        user.DeviceOtpPendingDeviceIdHash = null;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.DeviceVerified, user.Id, user.Email, ip);

        // Operator visibility: surface abusive device accumulation without
        // blocking legitimate power users.
        var deviceCount = await _db.UserDevices.CountAsync(d => d.UserId == user.Id);
        if (deviceCount >= DeviceCountWarnThreshold)
        {
            _logger.LogWarning(
                "User {UserId} has {Count} verified devices — review for abuse",
                user.Id,
                deviceCount
            );
        }

        var response = IssueTokenPair(user, userAgent);
        await _db.SaveChangesAsync();
        return (response, 200, null);
    }

    /// <summary>
    /// Idempotent: registers the device for this user if it isn't already, or
    /// bumps LastSeenAt if it is. Caller is responsible for calling
    /// <c>SaveChanges</c>.
    /// </summary>
    private async Task EnsureDeviceTrustedAsync(User user, string deviceId, string? userAgent)
    {
        var now = DateTime.UtcNow;
        var devices = await _db
            .UserDevices.Where(d => d.UserId == user.Id)
            .Select(d => new { d.Id, d.DeviceIdHash })
            .ToListAsync();

        foreach (var d in devices)
        {
            if (PasswordHasher.VerifyOtp(deviceId, d.DeviceIdHash))
            {
                await _db
                    .UserDevices.Where(x => x.Id == d.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now));
                return;
            }
        }

        _db.UserDevices.Add(
            new UserDevice
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                DeviceIdHash = PasswordHasher.HashOtp(deviceId),
                FirstSeenAt = now,
                LastSeenAt = now,
                UserAgent = Truncate(userAgent, 512),
            }
        );
    }

    private async Task<(bool Matched, Guid? MatchedDeviceId)> TryMatchDeviceAsync(
        User user,
        string? deviceId
    )
    {
        if (string.IsNullOrEmpty(deviceId))
            return (false, null);

        var cutoff = DateTime.UtcNow - DeviceMaxAge;
        // Fetch the user's active (non-expired) devices. Count is bounded by
        // human behavior (single digits typically), so an in-memory VerifyOtp
        // scan is cheaper than storing a lookup index.
        var devices = await _db
            .UserDevices.Where(d => d.UserId == user.Id && d.LastSeenAt >= cutoff)
            .Select(d => new { d.Id, d.DeviceIdHash })
            .ToListAsync();

        foreach (var d in devices)
        {
            if (PasswordHasher.VerifyOtp(deviceId, d.DeviceIdHash))
                return (true, d.Id);
        }
        return (false, null);
    }

    private async Task<(int StatusCode, string? Error)> EnsureDeviceOtpSentAsync(
        User user,
        string? deviceId,
        string? ip
    )
    {
        if (
            user.LastDeviceOtpEmailAt.HasValue
            && DateTime.UtcNow - user.LastDeviceOtpEmailAt.Value < EmailCooldown
        )
        {
            return (429, "Please wait a few minutes before requesting another code.");
        }

        var code = PasswordHasher.GenerateSecureCode();
        user.DeviceOtpCode = PasswordHasher.HashOtp(code);
        user.DeviceOtpCodeExpiresAt = DateTime.UtcNow.Add(DeviceOtpCodeLifetime);
        user.DeviceOtpPendingDeviceIdHash = string.IsNullOrEmpty(deviceId)
            ? null
            : PasswordHasher.HashOtp(deviceId);
        user.LastDeviceOtpEmailAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.DeviceOtpRequested, user.Id, user.Email, ip);

        try
        {
            await _email.SendDeviceOtpCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send device OTP email");
            user.DeviceOtpCode = null;
            user.DeviceOtpCodeExpiresAt = null;
            user.DeviceOtpPendingDeviceIdHash = null;
            user.LastDeviceOtpEmailAt = null;
            await _db.SaveChangesAsync();
            return (503, "Failed to send email. Please try again.");
        }

        return (200, null);
    }

    private static string? Truncate(string? s, int max) =>
        s == null ? null
        : s.Length <= max ? s
        : s[..max];

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
            $"{oldName} -> {user.DisplayName}"
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
            return (null, 401, "Invalid email or password.");

        if (PasswordHasher.Verify(request.NewPassword, user.PasswordHash))
            return (null, 400, "New password must be different from current password.");

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString();
        await _refreshTokens.RevokeAllActiveAsync(user.Id);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.ChangePassword, user.Id, user.Email, ip);

        return (new MessageResponse("Password changed successfully."), 200, null);
    }

    public async Task<(AuthResponse? Response, int StatusCode, string? Error)> VerifyCodeAsync(
        VerifyCodeRequest request,
        string? ip = null,
        string? deviceId = null,
        string? userAgent = null
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

        if (!PasswordHasher.VerifyOtp(request.Code.Trim(), user.VerificationCode))
            return (null, 400, "Invalid or expired verification code.");

        user.EmailVerifiedAt = DateTime.UtcNow;
        user.VerificationCode = null;
        user.VerificationCodeExpiresAt = null;

        // The user just proved ownership of their email from this device, so
        // auto-trust it. Subsequent logins from the same deviceId skip the
        // new-device OTP challenge.
        if (!string.IsNullOrEmpty(deviceId))
            await EnsureDeviceTrustedAsync(user, deviceId, userAgent);

        await _audit.LogAsync(AuditEvent.VerifyEmail, user.Id, user.Email, ip);

        var response = IssueTokenPair(user, userAgent);
        await _db.SaveChangesAsync();
        return (response, 200, null);
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

        var code = PasswordHasher.GenerateSecureCode();
        user.VerificationCode = PasswordHasher.HashOtp(code);
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
            _logger.LogError(ex, "Failed to send verification email");
            user.VerificationCode = null;
            user.VerificationCodeExpiresAt = null;
            user.LastVerificationEmailAt = null;
            await _db.SaveChangesAsync();
            return (null, 503, "Failed to send email. Please try again.");
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

        var code = PasswordHasher.GenerateSecureCode();
        user.PasswordResetCode = PasswordHasher.HashOtp(code);
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
            _logger.LogError(ex, "Failed to send password reset email");
            user.PasswordResetCode = null;
            user.PasswordResetCodeExpiresAt = null;
            user.LastPasswordResetEmailAt = null;
            await _db.SaveChangesAsync();
            return (null, 503, "Failed to send email. Please try again.");
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

        if (!PasswordHasher.VerifyOtp(request.Code.Trim(), user.PasswordResetCode))
            return (null, 400, "Invalid or expired reset code.");

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.PasswordResetCode = null;
        user.PasswordResetCodeExpiresAt = null;
        // Invalidate all existing sessions — critical for security when password is reset
        user.SecurityStamp = Guid.NewGuid().ToString();
        await _refreshTokens.RevokeAllActiveAsync(user.Id);
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
            return (null, 401, "Invalid email or password.");

        var normalizedNewEmail = request.NewEmail.Trim().ToLowerInvariant();

        if (normalizedNewEmail == user.Email)
            return (null, 400, "New email is the same as current email.");

        // Silently accept even if email is taken — don't reveal existence to the requester.
        // The confirmation step will catch the race condition anyway.
        if (await _db.Users.AnyAsync(u => u.Email == normalizedNewEmail))
            return (
                new MessageResponse("A confirmation code has been sent to your new email address."),
                200,
                null
            );

        // Generate confirmation code for the new email
        var code = PasswordHasher.GenerateSecureCode();
        user.PendingEmail = normalizedNewEmail;
        user.PendingEmailCode = PasswordHasher.HashOtp(code);
        user.PendingEmailCodeExpiresAt = DateTime.UtcNow.Add(VerificationCodeLifetime);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditEvent.ChangeEmail, user.Id, user.Email, ip, normalizedNewEmail);

        // Send verification code to new email
        try
        {
            await _email.SendEmailChangeCodeAsync(normalizedNewEmail, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email change verification");
            user.PendingEmail = null;
            user.PendingEmailCode = null;
            user.PendingEmailCodeExpiresAt = null;
            await _db.SaveChangesAsync();
            return (null, 503, "Failed to send email. Please try again.");
        }

        // Notify old email
        try
        {
            await _email.SendEmailChangeNotificationAsync(user.Email, normalizedNewEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email change notification");
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

        if (!PasswordHasher.VerifyOtp(request.Code.Trim(), user.PendingEmailCode))
            return (null, 400, "Invalid or expired confirmation code.");

        // Fast-path check: the new email hasn't been taken since the change was requested.
        // This is advisory only — the unique index on Users.Email is the authoritative guard
        // against a concurrent confirmation winning between this check and SaveChangesAsync.
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

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Concurrent confirmation won the race; reload the row, clear the pending
            // state, and report the conflict. The unique index on Users.Email ensures
            // exactly one confirmation succeeds.
            await _db.Entry(user).ReloadAsync();
            user.PendingEmail = null;
            user.PendingEmailCode = null;
            user.PendingEmailCodeExpiresAt = null;
            await _db.SaveChangesAsync();
            return (null, 409, "An account with this email already exists.");
        }

        await _audit.LogAsync(AuditEvent.ConfirmEmailChange, user.Id, user.Email, ip, oldEmail);

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
        await _refreshTokens.RevokeAllActiveAsync(user.Id);

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
        // Invalidate all pre-lock sessions
        user.SecurityStamp = Guid.NewGuid().ToString();
        await _refreshTokens.RevokeAllActiveAsync(user.Id);

        // Generate a password reset code so the user can set a new password
        var code = PasswordHasher.GenerateSecureCode();
        user.PasswordResetCode = PasswordHasher.HashOtp(code);
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
            _logger.LogError(ex, "Failed to send password reset email");
            user.PasswordResetCode = null;
            user.PasswordResetCodeExpiresAt = null;
            await _db.SaveChangesAsync();
            return (null, 503, "Failed to send email. Please try again.");
        }

        return (
            new MessageResponse($"Account '{user.Email}' unlocked. Password reset email sent."),
            200,
            null
        );
    }

    // Npgsql raises SQLSTATE 23505 on unique-constraint violation.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == "23505";

    /// <summary>
    /// Rotates a refresh token. On success returns a fresh access JWT + refresh
    /// pair. On failure (unknown / expired / reused / revoked token) returns 401
    /// without leaking which sub-check failed.
    /// </summary>
    public async Task<(AuthResponse? Response, int StatusCode, string? Error)> RefreshAsync(
        string rawRefreshToken,
        string? userAgent = null
    )
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
            return (null, 400, "Refresh token is required.");

        var (issue, user) = await _refreshTokens.RedeemAsync(rawRefreshToken, userAgent);
        if (issue == null || user == null)
            return (null, 401, "Invalid or expired refresh token.");

        if (user.IsLocked)
            return (null, 403, "Account is locked. Please contact support on Discord.");

        var jwt = _jwt.GenerateToken(user);
        await _db.SaveChangesAsync();

        return (
            new AuthResponse(
                Token: jwt,
                DisplayName: user.DisplayName,
                RefreshToken: issue.Token,
                ExpiresIn: (int)JwtHelper.AccessTokenLifetime.TotalSeconds,
                RefreshExpiresIn: (int)RefreshTokenService.Lifetime.TotalSeconds
            ),
            200,
            null
        );
    }

    /// <summary>
    /// Best-effort logout — revokes the supplied refresh token if it exists,
    /// always returns 200. Idempotent so the client can call it from a "user
    /// hit log out" handler without having to handle errors.
    /// </summary>
    public async Task<(MessageResponse Response, int StatusCode)> LogoutAsync(
        string rawRefreshToken
    )
    {
        if (!string.IsNullOrEmpty(rawRefreshToken))
        {
            await _refreshTokens.RevokeAsync(rawRefreshToken);
            await _db.SaveChangesAsync();
        }
        return (new MessageResponse("Logged out."), 200);
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
