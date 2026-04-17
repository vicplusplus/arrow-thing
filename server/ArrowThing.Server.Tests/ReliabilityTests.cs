using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 2 reliability tests: global exception handler / correlation ids, and
/// email-failure surfacing. Redis-unavailable tests are in a dedicated class
/// (<see cref="RedisUnavailableTests"/>) because they need a separate factory
/// override that stubs out the multiplexer before app startup.
/// </summary>
public class ReliabilityTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;
    private readonly string _deviceId = Guid.NewGuid().ToString("N");

    public ReliabilityTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", _deviceId);

        // Each test resets the throw flag; Dispose also resets it so a failure
        // mid-test doesn't leak throw-on-send into the next class.
        _factory.FakeEmail.ThrowOnSend = false;
    }

    public void Dispose()
    {
        _factory.FakeEmail.ThrowOnSend = false;
        _client.Dispose();
    }

    // -- Global exception middleware --

    [Fact]
    public async Task Crash_ReturnsStandardizedErrorWithCorrelationId()
    {
        using var response = await _client.GetAsync("/__test/crash");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.True(
            response.Headers.TryGetValues("X-Correlation-Id", out var headerValues),
            "Response must carry X-Correlation-Id"
        );
        var headerId = headerValues!.First();
        Assert.False(string.IsNullOrEmpty(headerId));

        var body = await response.Content.ReadFromJsonAsync<CrashErrorBody>();
        Assert.NotNull(body);
        Assert.Equal("Internal server error.", body!.error);
        Assert.Equal(headerId, body.correlationId);
    }

    [Fact]
    public async Task Crash_PreservesClientSuppliedCorrelationId()
    {
        const string clientId = "client-supplied-correlation";

        var request = new HttpRequestMessage(HttpMethod.Get, "/__test/crash");
        request.Headers.Add("X-Correlation-Id", clientId);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(clientId, response.Headers.GetValues("X-Correlation-Id").First());

        var body = await response.Content.ReadFromJsonAsync<CrashErrorBody>();
        Assert.Equal(clientId, body!.correlationId);
    }

    [Fact]
    public async Task SuccessResponse_AlsoCarriesCorrelationId()
    {
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    // -- Email failure surfacing --

    [Fact]
    public async Task Register_EmailOutage_Returns503AndClearsCode()
    {
        _factory.FakeEmail.ThrowOnSend = true;

        const string email = "register-outage@example.com";
        using var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "Password123!",
                displayName = "Outage",
            }
        );

        Assert.Equal((HttpStatusCode)503, response.StatusCode);

        // The code must have been cleared so the user isn't blocked by the
        // 5-minute cooldown on their next attempt.
        await AssertUserFieldCleared(
            email,
            u => u.VerificationCode,
            u => u.LastVerificationEmailAt
        );
    }

    [Fact]
    public async Task ForgotPassword_EmailOutage_Returns503AndClearsCode()
    {
        await RegisterAndVerifyAsync("forgot-outage@example.com");
        _factory.FakeEmail.ThrowOnSend = true;

        using var response = await _client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = "forgot-outage@example.com" }
        );

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
        await AssertUserFieldCleared(
            "forgot-outage@example.com",
            u => u.PasswordResetCode,
            u => u.LastPasswordResetEmailAt
        );
    }

    [Fact]
    public async Task Login_DeviceOtpEmailOutage_Returns503AndClearsPendingState()
    {
        await RegisterAndVerifyAsync("device-outage@example.com");

        // New device id — unknown to the server, so login triggers the OTP
        // email path.
        var otherDeviceId = Guid.NewGuid().ToString("N");
        _factory.FakeEmail.ThrowOnSend = true;

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(
                new { email = "device-outage@example.com", password = "Password123!" }
            ),
        };
        req.Headers.Remove("X-Device-Id");
        req.Headers.Add("X-Device-Id", otherDeviceId);
        using var response = await _client.SendAsync(req);

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
        await AssertUserFieldCleared(
            "device-outage@example.com",
            u => u.DeviceOtpCode,
            u => u.LastDeviceOtpEmailAt
        );
    }

    [Fact]
    public async Task ChangeEmail_EmailOutage_Returns503AndClearsPending()
    {
        await RegisterAndVerifyAsync("change-outage@example.com");

        var token = await LoginAndGetTokenAsync("change-outage@example.com");
        _factory.FakeEmail.ThrowOnSend = true;

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-email")
        {
            Content = JsonContent.Create(
                new { newEmail = "change-new@example.com", currentPassword = "Password123!" }
            ),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(req);

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
        await AssertUserFieldCleared(
            "change-outage@example.com",
            u => u.PendingEmail,
            u => u.PendingEmailCodeExpiresAt
        );
    }

    // -- Helpers --

    private async Task RegisterAndVerifyAsync(string email)
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "Password123!",
                displayName = "Reliability",
            }
        );
        var code = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == email && e.Type == "verification"
        );
        var verify = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email, code = code!.Token }
        );
        verify.EnsureSuccessStatusCode();
    }

    private async Task<string> LoginAndGetTokenAsync(string email)
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = "Password123!" }
        );
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    private async Task AssertUserFieldCleared<T1, T2>(
        string email,
        Func<User, T1> field,
        Func<User, T2> cooldownField
    )
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Email == email);
        Assert.Null(field(user));
        Assert.Null(cooldownField(user));
    }

    private class CrashErrorBody
    {
        public string error { get; set; } = "";
        public string correlationId { get; set; } = "";
    }
}
