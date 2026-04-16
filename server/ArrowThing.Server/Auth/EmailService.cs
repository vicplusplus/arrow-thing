using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ArrowThing.Server.Auth;

public class EmailService : IEmailService
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string? _fromAddress;

    public EmailService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient("Resend");
        _apiKey = configuration["Resend:ApiKey"];
        _fromAddress = configuration["Resend:FromAddress"];
    }

    public async Task SendVerificationCodeAsync(string toEmail, string code)
    {
        var html = $"""
            <h2>Verify your Arrow Thing account</h2>
            <p>Your verification code is:</p>
            <p style="font-size:32px;font-weight:bold;letter-spacing:6px;text-align:center;padding:16px;background:#2a2a3e;border-radius:8px;color:#7c8aff">{code}</p>
            <p>Enter this code in the game to verify your email. It expires in 10 minutes.</p>
            <p>If you didn't create an account, you can ignore this email.</p>
            """;

        await SendAsync(toEmail, "Your Arrow Thing verification code", html);
    }

    public async Task SendAlreadyRegisteredEmailAsync(string toEmail)
    {
        var html = """
            <h2>Arrow Thing account</h2>
            <p>Someone tried to create an Arrow Thing account with this email address, but you already have one.</p>
            <p>If this was you, try logging in instead. You can use "Forgot password?" if you've forgotten your password.</p>
            <p>If this wasn't you, you can ignore this email — your account is safe.</p>
            """;

        await SendAsync(toEmail, "Arrow Thing: account registration attempt", html);
    }

    public async Task SendPasswordResetCodeAsync(string toEmail, string code)
    {
        var html = $"""
            <h2>Reset your Arrow Thing password</h2>
            <p>Your password reset code is:</p>
            <p style="font-size:32px;font-weight:bold;letter-spacing:6px;text-align:center;padding:16px;background:#2a2a3e;border-radius:8px;color:#7c8aff">{code}</p>
            <p>Enter this code in the game to reset your password. It expires in 10 minutes.</p>
            <p>If you didn't request a password reset, you can ignore this email.</p>
            """;

        await SendAsync(toEmail, "Reset your Arrow Thing password", html);
    }

    public async Task SendEmailChangeCodeAsync(string toNewEmail, string code)
    {
        var html = $"""
            <h2>Confirm your new email address</h2>
            <p>Your email change confirmation code is:</p>
            <p style="font-size:32px;font-weight:bold;letter-spacing:6px;text-align:center;padding:16px;background:#2a2a3e;border-radius:8px;color:#7c8aff">{code}</p>
            <p>Enter this code in the game to confirm your new email. It expires in 10 minutes.</p>
            <p>If you didn't request this change, you can ignore this email.</p>
            """;

        await SendAsync(toNewEmail, "Confirm your new Arrow Thing email", html);
    }

    public async Task SendEmailChangeNotificationAsync(string toOldEmail, string newEmail)
    {
        var safeNewEmail = System.Net.WebUtility.HtmlEncode(newEmail);
        var html = $"""
            <h2>Email change requested</h2>
            <p>Someone requested to change the email on your Arrow Thing account to <strong>{safeNewEmail}</strong>.</p>
            <p>If this was you, no action is needed — just confirm via the link sent to your new email.</p>
            <p>If this wasn't you, please reach out to us on <a href="https://discord.gg/FBwTyaWzpE">Discord</a> so we can help secure your account.</p>
            """;

        await SendAsync(toOldEmail, "Arrow Thing: email change requested", html);
    }

    public async Task SendDeviceOtpCodeAsync(string toEmail, string code)
    {
        var html = $"""
            <h2>New-device login attempt</h2>
            <p>Someone tried to log in to your Arrow Thing account from a device we don't recognize. If that was you, enter this code in the game to complete the sign-in:</p>
            <p style="font-size:32px;font-weight:bold;letter-spacing:6px;text-align:center;padding:16px;background:#2a2a3e;border-radius:8px;color:#7c8aff">{code}</p>
            <p>This code expires in 10 minutes.</p>
            <p>If this wasn't you, you can ignore this email and consider changing your password. Someone may know it.</p>
            """;

        await SendAsync(toEmail, "Arrow Thing: new-device sign-in code", html);
    }

    private async Task SendAsync(string to, string subject, string html)
    {
        if (_apiKey == null || _fromAddress == null)
            throw new InvalidOperationException(
                "Resend:ApiKey and Resend:FromAddress are not configured."
            );

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _apiKey
        );

        var payload = new
        {
            from = _fromAddress,
            to = new[] { to },
            subject,
            html,
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _http.PostAsync("https://api.resend.com/emails", content);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Resend API error ({response.StatusCode}): {body}"
            );
        }
    }
}
