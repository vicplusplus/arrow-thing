using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;

namespace ArrowThing.Server.Tests;

public class LobbyTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public LobbyTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private async Task<AuthResponse> RegisterAndVerifyAsync(string email)
    {
        // Display name capped at 24 chars by the model.
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

    // -- Create --

    [Fact]
    public async Task Create_RequiresAuth()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Test" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithSixCharCode()
    {
        var auth = await RegisterAndVerifyAsync($"create-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.PostAsJsonAsync(
            "/api/lobbies",
            new { name = "My First Lobby" }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LobbyResponse>();
        Assert.NotNull(body);
        Assert.Equal(6, body!.Code.Length);
        Assert.Equal("My First Lobby", body.Name);
        // TestFactory overrides LobbyOptions to 20x20 for fast generation in tests.
        Assert.Equal(20, body.Width);
        Assert.Equal(20, body.Height);
        Assert.Contains(body.Code, body.ShareUrl);
    }

    [Fact]
    public async Task Create_EmptyName_Returns400()
    {
        var auth = await RegisterAndVerifyAsync($"empty-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.PostAsJsonAsync("/api/lobbies", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_NameTooLong_Returns400()
    {
        var auth = await RegisterAndVerifyAsync($"long-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.PostAsJsonAsync(
            "/api/lobbies",
            new { name = new string('x', 41) }
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_StripsControlChars()
    {
        var auth = await RegisterAndVerifyAsync($"strip-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var response = await _client.PostAsJsonAsync(
            "/api/lobbies",
            new { name = "Hello\u0000World\u0007" }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LobbyResponse>();
        Assert.Equal("HelloWorld", body!.Name);
    }

    [Fact]
    public async Task Create_FifthLobby_Succeeds_SixthFails()
    {
        var auth = await RegisterAndVerifyAsync($"cap-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        for (int i = 1; i <= 5; i++)
        {
            var ok = await _client.PostAsJsonAsync("/api/lobbies", new { name = $"Lobby {i}" });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var sixth = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Lobby 6" });
        Assert.Equal((HttpStatusCode)429, sixth.StatusCode);
    }

    // -- Get by code --

    [Fact]
    public async Task GetByCode_Existing_ReturnsLobby()
    {
        var auth = await RegisterAndVerifyAsync($"get-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Findable" });
        var created = await create.Content.ReadFromJsonAsync<LobbyResponse>();

        var get = await _client.GetAsync($"/api/lobbies/{created!.Code}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<LobbyResponse>();
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal("Findable", body.Name);
    }

    [Fact]
    public async Task GetByCode_CaseInsensitive()
    {
        var auth = await RegisterAndVerifyAsync($"caseins-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Case Test" });
        var created = await create.Content.ReadFromJsonAsync<LobbyResponse>();

        var get = await _client.GetAsync($"/api/lobbies/{created!.Code.ToLowerInvariant()}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task GetByCode_Missing_Returns404()
    {
        var auth = await RegisterAndVerifyAsync($"missing-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var get = await _client.GetAsync("/api/lobbies/ZZZZZZ");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // -- List mine --

    [Fact]
    public async Task ListMine_ReturnsOnlyMyLobbies()
    {
        var alice = await RegisterAndVerifyAsync($"alice-{Guid.NewGuid():N}@example.com");
        var bob = await RegisterAndVerifyAsync($"bob-{Guid.NewGuid():N}@example.com");

        SetAuth(alice.Token);
        await _client.PostAsJsonAsync("/api/lobbies", new { name = "Alice 1" });
        await _client.PostAsJsonAsync("/api/lobbies", new { name = "Alice 2" });

        SetAuth(bob.Token);
        await _client.PostAsJsonAsync("/api/lobbies", new { name = "Bob 1" });

        var resp = await _client.GetAsync("/api/lobbies/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LobbyListResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Entries);
        Assert.Equal("Bob 1", body.Entries[0].Name);
    }

    // -- Soft delete --

    [Fact]
    public async Task Delete_Owner_Succeeds()
    {
        var auth = await RegisterAndVerifyAsync($"del-{Guid.NewGuid():N}@example.com");
        SetAuth(auth.Token);

        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Deletable" });
        var created = await create.Content.ReadFromJsonAsync<LobbyResponse>();

        var del = await _client.DeleteAsync($"/api/lobbies/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // Code is now unreachable
        var get = await _client.GetAsync($"/api/lobbies/{created.Code}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Delete_NonOwner_Returns403()
    {
        var alice = await RegisterAndVerifyAsync($"owner-{Guid.NewGuid():N}@example.com");
        var bob = await RegisterAndVerifyAsync($"thief-{Guid.NewGuid():N}@example.com");

        SetAuth(alice.Token);
        var create = await _client.PostAsJsonAsync("/api/lobbies", new { name = "Mine" });
        var created = await create.Content.ReadFromJsonAsync<LobbyResponse>();

        SetAuth(bob.Token);
        var del = await _client.DeleteAsync($"/api/lobbies/{created!.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }
}
