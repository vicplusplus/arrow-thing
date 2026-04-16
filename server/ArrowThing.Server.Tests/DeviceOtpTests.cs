using System.Net;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Integration tests for the new-device OTP flow added in Phase 1B.
///
/// Each test builds its own <see cref="HttpClient"/> with an explicit
/// <c>X-Device-Id</c> header so the device identity is deterministic per
/// scenario (the default ctor-client would use one shared device for the
/// whole class, which doesn't exercise the "unknown device" branch).
/// </summary>
public class DeviceOtpTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;
    private readonly string _primaryDevice = Guid.NewGuid().ToString("N");

    public DeviceOtpTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", _primaryDevice);
    }

    public void Dispose() => _client.Dispose();

    // -- Registration auto-trusts the device --

    [Fact]
    public async Task VerifyCode_TrustsDeviceSoSubsequentLoginSkipsOtp()
    {
        await RegisterAndVerifyAsync("trusted@example.com");

        var login = await LoginAsync("trusted@example.com", _primaryDevice);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Null(
            _factory.FakeEmail.SentEmails.Find(e =>
                e.To == "trusted@example.com" && e.Type == "device-otp"
            )
        );

        var body = await login.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.Token));
    }

    // -- Unknown device triggers OTP --

    [Fact]
    public async Task Login_FromUnknownDevice_SendsOtpAndReturnsPending()
    {
        await RegisterAndVerifyAsync("new-device@example.com");
        var otherDevice = Guid.NewGuid().ToString("N");

        var login = await LoginAsync("new-device@example.com", otherDevice);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<DeviceOtpRequiredResponse>();
        Assert.True(body!.RequiresDeviceOtp);

        var otp = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == "new-device@example.com" && e.Type == "device-otp"
        );
        Assert.NotNull(otp);
    }

    [Fact]
    public async Task Login_NoDeviceIdHeader_SendsOtpAndReturnsPending()
    {
        await RegisterAndVerifyAsync("no-header@example.com");

        using var bareClient = _factory.CreateClient();
        var login = await bareClient.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "no-header@example.com", password = "Password123!" }
        );

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<DeviceOtpRequiredResponse>();
        Assert.True(body!.RequiresDeviceOtp);
    }

    // -- verify-device happy path --

    [Fact]
    public async Task VerifyDevice_CorrectCode_IssuesTokenAndStoresDevice()
    {
        await RegisterAndVerifyAsync("verify-ok@example.com");
        var otherDevice = Guid.NewGuid().ToString("N");
        await LoginAsync("verify-ok@example.com", otherDevice);

        var otp = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == "verify-ok@example.com" && e.Type == "device-otp"
        );
        Assert.NotNull(otp);

        var verified = await VerifyDeviceAsync(
            "verify-ok@example.com",
            "Password123!",
            otp!.Token,
            otherDevice
        );
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var body = await verified.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.Token));

        // Second login from the same device must skip OTP.
        var again = await LoginAsync("verify-ok@example.com", otherDevice);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var secondBody = await again.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(secondBody);
        Assert.False(string.IsNullOrEmpty(secondBody!.Token));
    }

    // -- verify-device rejection paths --

    [Fact]
    public async Task VerifyDevice_WrongCode_Returns400()
    {
        await RegisterAndVerifyAsync("wrong-code@example.com");
        var otherDevice = Guid.NewGuid().ToString("N");
        await LoginAsync("wrong-code@example.com", otherDevice);

        var verified = await VerifyDeviceAsync(
            "wrong-code@example.com",
            "Password123!",
            "000000",
            otherDevice
        );
        Assert.Equal(HttpStatusCode.BadRequest, verified.StatusCode);
    }

    [Fact]
    public async Task VerifyDevice_WrongPassword_Returns401()
    {
        await RegisterAndVerifyAsync("wrong-pw@example.com");
        var otherDevice = Guid.NewGuid().ToString("N");
        await LoginAsync("wrong-pw@example.com", otherDevice);

        var otp = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == "wrong-pw@example.com" && e.Type == "device-otp"
        );

        var verified = await VerifyDeviceAsync(
            "wrong-pw@example.com",
            "WrongPassword!",
            otp!.Token,
            otherDevice
        );
        Assert.Equal(HttpStatusCode.Unauthorized, verified.StatusCode);
    }

    [Fact]
    public async Task VerifyDevice_FromDifferentDeviceThanRequested_Returns400()
    {
        await RegisterAndVerifyAsync("wrong-device@example.com");
        var requestingDevice = Guid.NewGuid().ToString("N");
        await LoginAsync("wrong-device@example.com", requestingDevice);

        var otp = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == "wrong-device@example.com" && e.Type == "device-otp"
        );

        // Attempt to redeem the code from a different device id. The OTP is
        // bound to the requesting device so this must fail.
        var attackerDevice = Guid.NewGuid().ToString("N");
        var verified = await VerifyDeviceAsync(
            "wrong-device@example.com",
            "Password123!",
            otp!.Token,
            attackerDevice
        );
        Assert.Equal(HttpStatusCode.BadRequest, verified.StatusCode);
    }

    // -- Rate limit --

    [Fact]
    public async Task Login_OtpRequestedTwiceInCooldown_Returns429()
    {
        await RegisterAndVerifyAsync("rate-limit@example.com");
        var otherDevice = Guid.NewGuid().ToString("N");

        var first = await LoginAsync("rate-limit@example.com", otherDevice);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await LoginAsync("rate-limit@example.com", otherDevice);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
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
                displayName = "Device Tester",
            }
        );

        var code = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == email && e.Type == "verification"
        );
        Assert.NotNull(code);

        var resp = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email, code = code!.Token }
        );
        resp.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> LoginAsync(string email, string deviceId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password = "Password123!" }),
        };
        req.Headers.Remove("X-Device-Id");
        req.Headers.Add("X-Device-Id", deviceId);
        return await _client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> VerifyDeviceAsync(
        string email,
        string password,
        string code,
        string deviceId
    )
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/verify-device")
        {
            Content = JsonContent.Create(
                new
                {
                    email,
                    password,
                    code,
                }
            ),
        };
        req.Headers.Remove("X-Device-Id");
        req.Headers.Add("X-Device-Id", deviceId);
        return await _client.SendAsync(req);
    }
}
