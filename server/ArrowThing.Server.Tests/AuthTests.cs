using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;

namespace ArrowThing.Server.Tests;

public class AuthTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public AuthTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Helper: register a user and verify their email via code, returning an AuthResponse with JWT.
    /// </summary>
    private async Task<AuthResponse> RegisterAndVerifyAsync(
        string email,
        string password,
        string displayName
    )
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password,
                displayName,
            }
        );

        var code = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == email && e.Type == "verification"
        );

        var verifyResponse = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email, code = code!.Token }
        );
        var auth = await verifyResponse.Content.ReadFromJsonAsync<AuthResponse>();
        return auth!;
    }

    // -- Register --

    [Fact]
    public async Task Register_ValidRequest_ReturnsMessageAndSendsCode()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "test@example.com",
                password = "password123",
                displayName = "Test User",
            }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>();
        Assert.NotNull(body);
        Assert.Contains("verification code", body.Message);

        // Verification code email was sent
        Assert.Contains(
            _factory.FakeEmail.SentEmails,
            e => e.To == "test@example.com" && e.Type == "verification"
        );
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns200WithNotificationEmail()
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "shared@example.com",
                password = "password123",
                displayName = "First",
            }
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "shared@example.com",
                password = "password456",
                displayName = "Second",
            }
        );

        // Same 200 response — no info leak
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // "Already registered" notification sent to the existing owner
        Assert.Contains(
            _factory.FakeEmail.SentEmails,
            e => e.To == "shared@example.com" && e.Type == "already-registered"
        );
    }

    [Theory]
    [InlineData("notanemail", "password123", "Name")] // invalid email
    [InlineData("", "password123", "Name")] // empty email
    [InlineData("a@b.com", "short", "Name")] // password too short
    [InlineData("a@b.com", "password123", "")] // empty display name
    public async Task Register_InvalidInput_Returns400(
        string email,
        string password,
        string displayName
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password,
                displayName,
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Verify Code --

    [Fact]
    public async Task VerifyCode_ValidCode_ReturnsTokenAndVerifiesEmail()
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "verify@example.com",
                password = "password123",
                displayName = "Verify",
            }
        );

        var codeEmail = _factory.FakeEmail.SentEmails.Find(e =>
            e.To == "verify@example.com" && e.Type == "verification"
        );
        Assert.NotNull(codeEmail);

        var response = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email = "verify@example.com", code = codeEmail.Token }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.False(string.IsNullOrEmpty(auth.Token));
        Assert.False(string.IsNullOrEmpty(auth.DisplayName));
    }

    [Fact]
    public async Task VerifyCode_InvalidCode_Returns400()
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "verify-bad@example.com",
                password = "password123",
                displayName = "Bad Code",
            }
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email = "verify-bad@example.com", code = "000000" }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Login --

    [Fact]
    public async Task Login_VerifiedUser_ReturnsToken()
    {
        var auth = await RegisterAndVerifyAsync("login@example.com", "password123", "Login User");

        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "login@example.com", password = "password123" }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.Token));
        Assert.Equal("Login User", body.DisplayName);
    }

    [Fact]
    public async Task Login_UnverifiedUser_Returns401()
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "unverified-login@example.com",
                password = "password123",
                displayName = "Unverified",
            }
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "unverified-login@example.com", password = "password123" }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        await RegisterAndVerifyAsync("wrongpw@example.com", "password123", "User");

        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "wrongpw@example.com", password = "wrongpassword" }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_NonexistentUser_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "noexist@example.com", password = "password123" }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -- Protected endpoint (PATCH /api/auth/me) --

    [Fact]
    public async Task UpdateDisplayName_WithToken_Succeeds()
    {
        var auth = await RegisterAndVerifyAsync("dn@example.com", "password123", "Old Name");

        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/auth/me")
        {
            Content = JsonContent.Create(new { displayName = "New Name" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DisplayNameResponse>();
        Assert.Equal("New Name", body!.DisplayName);
    }

    [Fact]
    public async Task UpdateDisplayName_WithoutToken_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/auth/me")
        {
            Content = JsonContent.Create(new { displayName = "New Name" }),
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateDisplayName_InvalidName_Returns400()
    {
        var auth = await RegisterAndVerifyAsync("dnval@example.com", "password123", "Valid");

        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/auth/me")
        {
            Content = JsonContent.Create(new { displayName = "" }), // empty
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Forgot Password / Reset --

    [Fact]
    public async Task ForgotPassword_ExistingEmail_Returns200()
    {
        await RegisterAndVerifyAsync("forgot@example.com", "password123", "Forgot");

        var response = await _client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = "forgot@example.com" }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_NonexistentEmail_StillReturns200()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = "nobody@example.com" }
        );

        // Same response to prevent email enumeration
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_ValidCode_Succeeds()
    {
        await RegisterAndVerifyAsync("reset@example.com", "password123", "Reset");

        await _client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = "reset@example.com" }
        );

        var resetEmail = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == "reset@example.com" && e.Type == "reset"
        );
        Assert.NotNull(resetEmail);

        var response = await _client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new
            {
                email = "reset@example.com",
                code = resetEmail.Token,
                newPassword = "newpassword456",
            }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Old password should fail
        var oldLogin = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "reset@example.com", password = "password123" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        // New password should work
        var newLogin = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "reset@example.com", password = "newpassword456" }
        );
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_InvalidCode_Returns400()
    {
        await RegisterAndVerifyAsync("reset-bad@example.com", "password123", "ResetBad");

        await _client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = "reset-bad@example.com" }
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new
            {
                email = "reset-bad@example.com",
                code = "000000",
                newPassword = "newpassword456",
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_ShortPassword_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new
            {
                email = "anyone@example.com",
                code = "123456",
                newPassword = "short",
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Resend Verification --

    [Fact]
    public async Task ResendVerification_UnverifiedUser_RateLimited()
    {
        // Registration sends a verification code, so immediate resend should be rate-limited
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "resend@example.com",
                password = "password123",
                displayName = "Resend",
            }
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/resend-verification",
            new { email = "resend@example.com" }
        );

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task ResendVerification_NonexistentEmail_StillReturns200()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/resend-verification",
            new { email = "nobody@example.com" }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -- Change Password --

    [Fact]
    public async Task ChangePassword_ValidRequest_Succeeds()
    {
        var auth = await RegisterAndVerifyAsync(
            "changepw@example.com",
            "oldpassword123",
            "PW User"
        );
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "oldpassword123", newPassword = "newpassword456" }
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Old token should be invalidated (security stamp bumped)
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        var me = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        // Can log in with new password
        _client.DefaultRequestHeaders.Authorization = null;
        var login = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "changepw@example.com", password = "newpassword456" }
        );
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns401()
    {
        var auth = await RegisterAndVerifyAsync(
            "changepw-wrong@example.com",
            "password123",
            "PW User 2"
        );
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "wrongpassword", newPassword = "newpassword456" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ShortNewPassword_Returns400()
    {
        var auth = await RegisterAndVerifyAsync(
            "changepw-short@example.com",
            "password123",
            "PW User 3"
        );
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "password123", newPassword = "short" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_SamePassword_Returns400()
    {
        var auth = await RegisterAndVerifyAsync(
            "changepw-same@example.com",
            "password123",
            "PW User 4"
        );
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "password123", newPassword = "password123" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Email Change --

    [Fact]
    public async Task ChangeEmail_ValidRequest_SendsBothEmails()
    {
        var auth = await RegisterAndVerifyAsync("old@example.com", "password123", "Changer");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-email")
        {
            Content = JsonContent.Create(
                new { newEmail = "new@example.com", currentPassword = "password123" }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verification sent to new email
        Assert.Contains(
            _factory.FakeEmail.SentEmails,
            e => e.To == "new@example.com" && e.Type == "email-change"
        );
        // Notification sent to old email
        Assert.Contains(_factory.FakeEmail.Notifications, e => e == "old@example.com");
    }

    [Fact]
    public async Task ChangeEmail_WrongPassword_Returns401()
    {
        var auth = await RegisterAndVerifyAsync("chgpw@example.com", "password123", "PW Check");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-email")
        {
            Content = JsonContent.Create(
                new { newEmail = "new2@example.com", currentPassword = "wrongpassword" }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangeEmail_WithoutToken_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-email")
        {
            Content = JsonContent.Create(
                new { newEmail = "new3@example.com", currentPassword = "password123" }
            ),
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmailChange_ValidCode_UpdatesEmail()
    {
        var auth = await RegisterAndVerifyAsync("confirmold@example.com", "password123", "Confirm");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );

        // Request email change
        await _client.PostAsJsonAsync(
            "/api/auth/change-email",
            new { newEmail = "confirmnew@example.com", currentPassword = "password123" }
        );

        // Get the confirmation code
        var changeEmail = _factory.FakeEmail.SentEmails.Find(e =>
            e.To == "confirmnew@example.com" && e.Type == "email-change"
        );
        Assert.NotNull(changeEmail);

        // Confirm with code
        var response = await _client.PostAsJsonAsync(
            "/api/auth/confirm-email-change",
            new { email = "confirmnew@example.com", code = changeEmail.Token }
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Login with new email should work
        _client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "confirmnew@example.com", password = "password123" }
        );
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmailChange_InvalidCode_Returns400()
    {
        var auth = await RegisterAndVerifyAsync(
            "confirmold2@example.com",
            "password123",
            "Confirm2"
        );
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );

        // Request email change to create a pending change
        await _client.PostAsJsonAsync(
            "/api/auth/change-email",
            new { newEmail = "confirmnew2@example.com", currentPassword = "password123" }
        );

        var response = await _client.PostAsJsonAsync(
            "/api/auth/confirm-email-change",
            new { email = "confirmnew2@example.com", code = "000000" }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangeEmail_SameAsCurrent_Returns400()
    {
        var auth = await RegisterAndVerifyAsync("same@example.com", "password123", "Same");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-email")
        {
            Content = JsonContent.Create(
                new { newEmail = "same@example.com", currentPassword = "password123" }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Admin: Lock Account --

    [Fact]
    public async Task LockAccount_InvalidatesSessionAndBlocksLogin()
    {
        var auth = await RegisterAndVerifyAsync("lockme@example.com", "password123", "Lock Me");

        // Lock the account
        var lockRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/lock-account")
        {
            Content = JsonContent.Create(new { email = "lockme@example.com" }),
        };
        lockRequest.Headers.Add("X-Admin-Key", "test-admin-key");

        var lockResponse = await _client.SendAsync(lockRequest);
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);

        // Old token should now be rejected (security stamp mismatch)
        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var meResponse = await _client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);

        // Login should be blocked with 403
        var loginResponse = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "lockme@example.com", password = "password123" }
        );
        Assert.Equal(HttpStatusCode.Forbidden, loginResponse.StatusCode);
    }

    [Fact]
    public async Task UnlockAccount_SendsResetEmailAndAllowsRecovery()
    {
        await RegisterAndVerifyAsync("unlockme@example.com", "password123", "Unlock Me");

        var lockRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/lock-account")
        {
            Content = JsonContent.Create(new { email = "unlockme@example.com" }),
        };
        lockRequest.Headers.Add("X-Admin-Key", "test-admin-key");
        await _client.SendAsync(lockRequest);

        // Unlock
        var unlockRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/unlock-account")
        {
            Content = JsonContent.Create(new { email = "unlockme@example.com" }),
        };
        unlockRequest.Headers.Add("X-Admin-Key", "test-admin-key");

        var unlockResponse = await _client.SendAsync(unlockRequest);
        Assert.Equal(HttpStatusCode.OK, unlockResponse.StatusCode);

        // A password reset email should have been sent
        Assert.Contains(
            _factory.FakeEmail.SentEmails,
            e => e.To == "unlockme@example.com" && e.Type == "reset"
        );

        // Use the reset code to set a new password
        var resetEmail = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == "unlockme@example.com" && e.Type == "reset"
        );
        var resetResponse = await _client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new
            {
                email = "unlockme@example.com",
                code = resetEmail!.Token,
                newPassword = "newpassword123",
            }
        );
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

        // Can now log in with new password
        var loginResponse = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "unlockme@example.com", password = "newpassword123" }
        );
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task LockAccount_WithoutAdminKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/lock-account")
        {
            Content = JsonContent.Create(new { email = "anyone@example.com" }),
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LockAccount_WrongAdminKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/lock-account")
        {
            Content = JsonContent.Create(new { email = "anyone@example.com" }),
        };
        request.Headers.Add("X-Admin-Key", "wrong-key");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -- Password Reset Invalidates Sessions --

    [Fact]
    public async Task ResetPassword_InvalidatesExistingSessions()
    {
        var auth = await RegisterAndVerifyAsync(
            "reset-session@example.com",
            "password123",
            "Reset Session"
        );

        // Request password reset
        await _client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = "reset-session@example.com" }
        );

        var resetEmail = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == "reset-session@example.com" && e.Type == "reset"
        );

        // Reset the password
        await _client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new
            {
                email = "reset-session@example.com",
                code = resetEmail!.Token,
                newPassword = "newpassword456",
            }
        );

        // Old token should now be rejected (security stamp was bumped)
        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var meResponse = await _client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
    }

    // -- Login Lockout --

    [Fact]
    public async Task Login_LocksOutAfterRepeatedFailures()
    {
        await RegisterAndVerifyAsync("lockout@example.com", "password123", "Lockout");

        // Fail 5 times
        for (var i = 0; i < 5; i++)
        {
            await _client.PostAsJsonAsync(
                "/api/auth/login",
                new { email = "lockout@example.com", password = "wrongpassword" }
            );
        }

        // 6th attempt — even with correct password — should be rate-limited
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "lockout@example.com", password = "password123" }
        );
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // -- Email Change Doesn't Leak Email Existence --

    [Fact]
    public async Task ChangeEmail_ToExistingEmail_Returns200NotEnumerable()
    {
        await RegisterAndVerifyAsync("existing@example.com", "password123", "Existing");
        var auth = await RegisterAndVerifyAsync("changer2@example.com", "password123", "Changer2");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-email")
        {
            Content = JsonContent.Create(
                new { newEmail = "existing@example.com", currentPassword = "password123" }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);

        // Should return 200 (not 409) to prevent email enumeration
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -- Health Check --

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -- GET /api/auth/me --

    [Fact]
    public async Task GetMe_WithValidToken_ReturnsUserInfo()
    {
        var auth = await RegisterAndVerifyAsync("getme@example.com", "password123", "Get Me");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(body);
        Assert.Equal("Get Me", body.DisplayName);
        Assert.Equal("getme@example.com", body.Email);
    }

    [Fact]
    public async Task GetMe_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -- Admin: Unlock without key --

    [Fact]
    public async Task UnlockAccount_WithoutAdminKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/unlock-account")
        {
            Content = JsonContent.Create(new { email = "anyone@example.com" }),
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -- Register: display name too long --

    [Fact]
    public async Task Register_DisplayNameTooLong_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "longname@example.com",
                password = "password123",
                displayName = "This display name is way too long for the limit",
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Login lockout resets on successful login --

    [Fact]
    public async Task Login_LockoutResetsAfterSuccessfulLogin()
    {
        await RegisterAndVerifyAsync("lockout-reset@example.com", "password123", "LockReset");

        // Fail 3 times (under the threshold of 5)
        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsJsonAsync(
                "/api/auth/login",
                new { email = "lockout-reset@example.com", password = "wrongpassword" }
            );
        }

        // Successful login should reset the counter
        var loginResponse = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "lockout-reset@example.com", password = "password123" }
        );
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // Fail 4 more times — should still be under the threshold since counter reset
        for (var i = 0; i < 4; i++)
        {
            await _client.PostAsJsonAsync(
                "/api/auth/login",
                new { email = "lockout-reset@example.com", password = "wrongpassword" }
            );
        }

        // Should still be able to log in (4 < 5 threshold)
        var secondLogin = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "lockout-reset@example.com", password = "password123" }
        );
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
    }
}
