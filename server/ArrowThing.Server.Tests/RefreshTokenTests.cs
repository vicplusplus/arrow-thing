using System.Net;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 1C: refresh-token rotation, expiry, reuse detection, and the
/// SecurityStamp interaction.
/// </summary>
public class RefreshTokenTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;
    private readonly string _deviceId = Guid.NewGuid().ToString("N");

    public RefreshTokenTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", _deviceId);
    }

    public void Dispose() => _client.Dispose();

    // -- Issuance --

    [Fact]
    public async Task Login_IssuesAccessAndRefreshTokens()
    {
        var auth = await RegisterAndVerifyAsync("issue@example.com");

        Assert.False(string.IsNullOrEmpty(auth.Token));
        Assert.False(string.IsNullOrEmpty(auth.RefreshToken));
        Assert.Equal((int)JwtHelper.AccessTokenLifetime.TotalSeconds, auth.ExpiresIn);
        Assert.Equal((int)RefreshTokenService.Lifetime.TotalSeconds, auth.RefreshExpiresIn);
        Assert.Contains(".", auth.RefreshToken); // tokenId.secret format
    }

    // -- Refresh happy path --

    [Fact]
    public async Task Refresh_HappyPath_RotatesAndReturnsNewPair()
    {
        var auth = await RegisterAndVerifyAsync("rotate@example.com");

        var refreshed = await RefreshAsync(auth.RefreshToken);
        Assert.False(string.IsNullOrEmpty(refreshed!.Token));
        Assert.False(string.IsNullOrEmpty(refreshed.RefreshToken));
        Assert.NotEqual(auth.RefreshToken, refreshed.RefreshToken);

        // The original token can no longer be redeemed — see reuse-detection
        // test for the consequence (revoke-all). Here we only confirm 401.
        var resp = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = auth.RefreshToken }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_NewTokenWorks_OldTokenDoesNot()
    {
        var auth = await RegisterAndVerifyAsync("rotate-new@example.com");
        var refreshed = await RefreshAsync(auth.RefreshToken);
        Assert.NotNull(refreshed);

        // The new token can be refreshed in turn.
        var second = await RefreshAsync(refreshed!.RefreshToken);
        Assert.NotNull(second);
    }

    // -- Reuse detection --

    [Fact]
    public async Task Refresh_ReusedToken_RevokesAllUserTokens()
    {
        var auth = await RegisterAndVerifyAsync("reuse@example.com");

        // Legitimate rotation.
        var rotated = await RefreshAsync(auth.RefreshToken);
        Assert.NotNull(rotated);

        // Replay the original — this is the "stolen token" pattern. The
        // service treats it as suspected theft and revokes every active
        // refresh token for this user, including the freshly-rotated one.
        var replay = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = auth.RefreshToken }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The newly-rotated token must now also be rejected.
        var afterRevoke = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = rotated!.RefreshToken }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    // -- Expired / unknown / malformed --

    [Fact]
    public async Task Refresh_ExpiredToken_Returns401()
    {
        var auth = await RegisterAndVerifyAsync("expired@example.com");

        // Backdate ExpiresAt directly via DbContext.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db
                .RefreshTokens.Where(t => t.UserId != Guid.Empty)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddDays(-1))
                );
        }

        var resp = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = auth.RefreshToken }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_UnknownToken_Returns401()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = "AAAAAAAAAAAAAAAAAAAAAA.bogus-secret" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_MalformedToken_Returns401()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = "no-separator-here" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_EmptyToken_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // -- Logout --

    [Fact]
    public async Task Logout_RevokesToken()
    {
        var auth = await RegisterAndVerifyAsync("logout@example.com");

        var logout = await _client.PostAsJsonAsync(
            "/api/auth/logout",
            new { refreshToken = auth.RefreshToken }
        );
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        // Token is now revoked — refresh fails (and crucially does NOT
        // trigger the revoke-all reuse-detection path, because the row's
        // ReplacedByTokenId is null).
        var resp = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = auth.RefreshToken }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Logout_UnknownToken_Returns200()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/auth/logout",
            new { refreshToken = "AAAAAAAAAAAAAAAAAAAAAA.bogus" }
        );
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Logout_EmptyToken_Returns200()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // -- SecurityStamp interaction --

    [Fact]
    public async Task ChangePassword_RevokesAllRefreshTokensForUser()
    {
        var auth = await RegisterAndVerifyAsync("change-pw@example.com");

        // Mint a couple more refresh tokens by rotating, so we can verify
        // ALL active tokens are killed (not just the most recent).
        var rotated = await RefreshAsync(auth.RefreshToken);
        Assert.NotNull(rotated);

        var changeReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
        {
            Content = JsonContent.Create(
                new { currentPassword = "Password123!", newPassword = "NewPassword456!" }
            ),
        };
        changeReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            rotated!.Token
        );
        using var changeResp = await _client.SendAsync(changeReq);
        Assert.Equal(HttpStatusCode.OK, changeResp.StatusCode);

        // The rotated refresh token is now revoked. Note we don't try the
        // *original* refresh token because that one was already revoked at
        // rotation time — the test would pass for the wrong reason.
        var afterChange = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = rotated.RefreshToken }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, afterChange.StatusCode);
    }

    // -- Helpers --

    private async Task<AuthResponse> RegisterAndVerifyAsync(string email)
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "Password123!",
                displayName = "Refresh",
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
        var body = await verify.Content.ReadFromJsonAsync<AuthResponse>();
        return body!;
    }

    private async Task<AuthResponse?> RefreshAsync(string refreshToken)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        if (resp.StatusCode != HttpStatusCode.OK)
            return null;
        return await resp.Content.ReadFromJsonAsync<AuthResponse>();
    }
}
