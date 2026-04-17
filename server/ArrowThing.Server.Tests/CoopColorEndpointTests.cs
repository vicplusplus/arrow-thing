using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;

namespace ArrowThing.Server.Tests;

public record TestMeResponse(string Email, string DisplayName, string CoopColor);

public class CoopColorEndpointTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public CoopColorEndpointTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private async Task<AuthResponse> RegisterAndVerifyAsync(string email)
    {
        var displayName = email.Split('@')[0];
        if (displayName.Length > 24)
            displayName = displayName.Substring(0, 24);

        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "password123",
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

    private void SetAuth(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

    // -- PATCH /api/auth/me/coop-color --

    [Fact]
    public async Task PatchCoopColor_RequiresAuth()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.PatchAsJsonAsync(
            "/api/auth/me/coop-color",
            new { coopColor = "#A1B2C3" }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PatchCoopColor_ValidHex_Returns200WithCanonicalUppercase()
    {
        var auth = await RegisterAndVerifyAsync($"color-valid-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.PatchAsJsonAsync(
            "/api/auth/me/coop-color",
            new { coopColor = "#a1b2c3" }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CoopColorResponse>();
        Assert.NotNull(body);
        Assert.Equal("#A1B2C3", body!.CoopColor);
    }

    [Fact]
    public async Task PatchCoopColor_InvalidHex_Returns400()
    {
        var auth = await RegisterAndVerifyAsync($"color-invalid-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.PatchAsJsonAsync(
            "/api/auth/me/coop-color",
            new { coopColor = "red" }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchCoopColor_MissingHash_Returns400()
    {
        var auth = await RegisterAndVerifyAsync($"color-nohash-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.PatchAsJsonAsync(
            "/api/auth/me/coop-color",
            new { coopColor = "A1B2C3" }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- GET /api/auth/me (coopColor field) --

    [Fact]
    public async Task GetMe_ReturnsDefaultPaletteColorWhenNull()
    {
        var auth = await RegisterAndVerifyAsync($"color-default-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TestMeResponse>();
        Assert.NotNull(body);
        Assert.NotNull(body!.CoopColor);
        // Palette fallback is a valid hex color
        Assert.Matches(@"^#[0-9A-F]{6}$", body.CoopColor);
    }

    [Fact]
    public async Task GetMe_ReturnsSavedColorAfterPatch()
    {
        var auth = await RegisterAndVerifyAsync($"color-saved-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        // Patch a custom color
        var patchResponse = await _client.PatchAsJsonAsync(
            "/api/auth/me/coop-color",
            new { coopColor = "#ff8800" }
        );
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        // GET /api/auth/me should return the saved color
        var response = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TestMeResponse>();
        Assert.NotNull(body);
        Assert.Equal("#FF8800", body!.CoopColor);
    }
}
