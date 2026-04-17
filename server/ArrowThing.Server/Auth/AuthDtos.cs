namespace ArrowThing.Server.Auth;

public record RegisterRequest(string Email, string Password, string DisplayName);

public record LoginRequest(string Email, string Password);

public record UpdateDisplayNameRequest(string DisplayName);

public record VerifyCodeRequest(string Email, string Code);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string Code, string NewPassword);

public record ResendVerificationRequest(string Email);

public record ChangeEmailRequest(string NewEmail, string CurrentPassword);

public record ConfirmEmailChangeRequest(string Email, string Code);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record VerifyDeviceRequest(string Email, string Password, string Code);

public record RefreshTokenRequest(string RefreshToken);

public record LogoutRequest(string RefreshToken);

public record AuthResponse(
    string Token,
    string DisplayName,
    string RefreshToken,
    int ExpiresIn,
    int RefreshExpiresIn
);

/// <summary>
/// Returned from <c>/api/auth/login</c> when the supplied <c>X-Device-Id</c>
/// isn't recognized for this user. The client prompts for the 6-digit email
/// code and then calls <c>/api/auth/verify-device</c>.
/// </summary>
public record DeviceOtpRequiredResponse(bool RequiresDeviceOtp = true);

public record DisplayNameResponse(string DisplayName);

public record MessageResponse(string Message);

public record MeResponse(string Email, string DisplayName);

public record LockAccountRequest(string Email);
