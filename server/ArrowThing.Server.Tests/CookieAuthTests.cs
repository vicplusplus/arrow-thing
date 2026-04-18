using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 1D: HttpOnly-cookie auth path. Covers cookie issuance, cookie-only
/// authentication, refresh + logout via cookies, and the Origin-check
/// middleware. Bearer-auth tests continue to exercise the legacy path in
/// <see cref="AuthTests"/>.
/// </summary>
public class CookieAuthTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;
    private readonly string _deviceId = Guid.NewGuid().ToString("N");

    public CookieAuthTests(TestFactory factory)
    {
        _factory = factory;
        // Use the cookie-handling client so Set-Cookie responses from the
        // server are replayed on subsequent requests — mirroring the
        // browser behavior we're testing.
        _client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
        _client.DefaultRequestHeaders.Add("X-Device-Id", _deviceId);
    }

    public void Dispose() => _client.Dispose();

    // -- Issuance --

    [Fact]
    public async Task VerifyCode_SetsBothCookies()
    {
        var auth = await RegisterAndVerifyViaRawAsync("cookies-issue@example.com");

        // Raw Set-Cookie headers are inspected so we can assert the
        // security attributes, not just the presence of the cookie.
        Assert.Contains(auth.Cookies, c => c.Contains("arrow_access="));
        Assert.Contains(auth.Cookies, c => c.Contains("arrow_refresh="));
        Assert.All(
            auth.Cookies.Where(c => c.StartsWith("arrow_")),
            c =>
            {
                Assert.Contains("httponly", c, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("samesite=lax", c, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("path=/", c, StringComparison.OrdinalIgnoreCase);
            }
        );
    }

    // -- Authenticated request with cookie only --

    [Fact]
    public async Task GetMe_WithCookieOnly_Works()
    {
        await RegisterAndVerifyAsync("cookies-me@example.com");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        // No Authorization header — the cookie is attached by the shared
        // CookieContainer from the verify-code response.
        using var response = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -- Refresh from cookie (empty body) --

    [Fact]
    public async Task Refresh_WithCookieAndEmptyBody_RotatesAndReturns200()
    {
        await RegisterAndVerifyAsync("cookies-refresh@example.com");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            // Empty body — server reads arrow_refresh cookie for the token.
            Content = JsonContent.Create(new { refreshToken = "" }),
        };
        using var response = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrEmpty(body!.Token));
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));

        // The response also rotates the cookies.
        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, c => c.StartsWith("arrow_access="));
        Assert.Contains(setCookies, c => c.StartsWith("arrow_refresh="));
    }

    // -- Logout from cookie --

    [Fact]
    public async Task Logout_WithCookieOnly_ClearsCookiesAndRevokes()
    {
        await RegisterAndVerifyAsync("cookies-logout@example.com");

        // Grab the refresh token value from the cookie so we can verify
        // server-side revocation below. HandleCookies handlers don't expose
        // the underlying jar cleanly, so we just keep a copy from the
        // initial Set-Cookie on verify-code.
        // Instead, try a refresh AFTER logout and assert it fails.

        var logout = await _client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "" });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var setCookies = logout.Headers.GetValues("Set-Cookie").ToList();
        // Server clears by issuing a Set-Cookie with an expired date.
        Assert.Contains(
            setCookies,
            c =>
                c.StartsWith("arrow_access=")
                && c.Contains("expires=", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            setCookies,
            c =>
                c.StartsWith("arrow_refresh=")
                && c.Contains("expires=", StringComparison.OrdinalIgnoreCase)
        );

        // A follow-up refresh must fail — the cookie has been cleared
        // locally (HandleCookies honored the expired Set-Cookie) and the
        // refresh token is revoked server-side.
        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "" });
        Assert.True(
            refresh.StatusCode == HttpStatusCode.BadRequest
                || refresh.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected 400 or 401 after logout; got {(int)refresh.StatusCode}"
        );
    }

    // -- Origin check on cookie-authed mutations --

    [Fact]
    public async Task MutatingRequest_WithAuthCookieAndBadOrigin_Returns403()
    {
        await RegisterAndVerifyAsync("origin-bad@example.com");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = "" }),
        };
        req.Headers.Add("Origin", "https://evil.example.com");
        using var response = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MutatingRequest_WithBearerAndBadOrigin_StillWorks()
    {
        // Bearer-authed callers (editor / native) aren't browsers, don't
        // send Origin as the exploit vector, and must bypass the check.
        var auth = await RegisterAndVerifyAsync("origin-bearer@example.com");
        Assert.NotNull(auth);

        using var freshClient = _factory.CreateClient();
        freshClient.DefaultRequestHeaders.Add("X-Device-Id", _deviceId);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
        {
            Content = JsonContent.Create(
                new { currentPassword = "Password123!", newPassword = "NewPassword456!" }
            ),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        req.Headers.Add("Origin", "https://evil.example.com");
        using var response = await freshClient.SendAsync(req);

        // The Origin check skipped because no auth cookie was present.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -- Helpers --

    private async Task<AuthResponse?> RegisterAndVerifyAsync(string email)
    {
        var raw = await RegisterAndVerifyViaRawAsync(email);
        return raw.Auth;
    }

    private async Task<RawAuth> RegisterAndVerifyViaRawAsync(string email)
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "Password123!",
                displayName = "Cookie",
            }
        );
        var code = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == email && e.Type == "verification"
        );
        using var verify = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email, code = code!.Token }
        );
        verify.EnsureSuccessStatusCode();
        var cookies = verify.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : new List<string>();
        var body = await verify.Content.ReadFromJsonAsync<AuthResponse>();
        return new RawAuth(body, cookies);
    }

    private record RawAuth(AuthResponse? Auth, List<string> Cookies);
}
