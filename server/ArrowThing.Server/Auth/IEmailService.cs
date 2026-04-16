namespace ArrowThing.Server.Auth;

public interface IEmailService
{
    Task SendVerificationCodeAsync(string toEmail, string code);
    Task SendAlreadyRegisteredEmailAsync(string toEmail);
    Task SendPasswordResetCodeAsync(string toEmail, string code);
    Task SendEmailChangeCodeAsync(string toNewEmail, string code);
    Task SendEmailChangeNotificationAsync(string toOldEmail, string newEmail);
    Task SendDeviceOtpCodeAsync(string toEmail, string code);
}
