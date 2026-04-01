using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ArrowThing.Server.Tests;

public class AdminTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;
    private const string AdminKey = "test-admin-key";

    public AdminTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private void SetAdminKey() => _client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);

    private async Task SeedUserAsync(string email, string displayName = "Test")
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new { email, password = "password123", displayName }
        );
    }

    // -- Stats --

    [Fact]
    public async Task Stats_WithoutKey_Returns401()
    {
        var response = await _client.GetAsync("/api/admin/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stats_WithKey_ReturnsStats()
    {
        SetAdminKey();
        var response = await _client.GetAsync("/api/admin/stats");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("totalUsers", out _));
        Assert.True(json.TryGetProperty("verifiedUsers", out _));
        Assert.True(json.TryGetProperty("lockedUsers", out _));
        Assert.True(json.TryGetProperty("logins24h", out _));
        Assert.True(json.TryGetProperty("failedLogins24h", out _));
        Assert.True(json.TryGetProperty("serverTime", out _));
    }

    // -- Users --

    [Fact]
    public async Task Users_WithoutKey_Returns401()
    {
        var response = await _client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_WithKey_ReturnsList()
    {
        SetAdminKey();
        await SeedUserAsync($"admin-test-{Guid.NewGuid():N}@example.com");

        var response = await _client.GetAsync("/api/admin/users");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("total").GetInt32() >= 1);
        Assert.True(json.GetProperty("users").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Users_Search_FiltersResults()
    {
        SetAdminKey();
        var unique = $"searchme-{Guid.NewGuid():N}@example.com";
        await SeedUserAsync(unique);

        var response = await _client.GetAsync($"/api/admin/users?search=searchme");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var users = json.GetProperty("users");
        Assert.True(users.GetArrayLength() >= 1);
        Assert.Contains("searchme", users[0].GetProperty("email").GetString());
    }

    // -- User detail --

    [Fact]
    public async Task UserDetail_NonExistent_Returns404()
    {
        SetAdminKey();
        var response = await _client.GetAsync($"/api/admin/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UserDetail_Existing_ReturnsUserAndLogs()
    {
        SetAdminKey();
        var email = $"detail-{Guid.NewGuid():N}@example.com";
        await SeedUserAsync(email, "DetailUser");

        // Get user ID from list
        var listResponse = await _client.GetAsync($"/api/admin/users?search={email}");
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = listJson.GetProperty("users")[0].GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/admin/users/{userId}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, json.GetProperty("user").GetProperty("email").GetString());
        Assert.True(json.TryGetProperty("recentLogs", out var logs));
        // Should have at least a register event
        Assert.True(logs.GetArrayLength() >= 1);
    }

    // -- Audit log --

    [Fact]
    public async Task AuditLog_WithoutKey_Returns401()
    {
        var response = await _client.GetAsync("/api/admin/audit-log");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuditLog_WithKey_ReturnsLogs()
    {
        SetAdminKey();
        // Seed some activity
        await SeedUserAsync($"audit-{Guid.NewGuid():N}@example.com");

        var response = await _client.GetAsync("/api/admin/audit-log");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("total").GetInt32() >= 1);
        Assert.True(json.GetProperty("logs").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task AuditLog_FilterByEvent_Works()
    {
        SetAdminKey();
        await SeedUserAsync($"filter-{Guid.NewGuid():N}@example.com");

        var response = await _client.GetAsync("/api/admin/audit-log?eventType=register");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var log in json.GetProperty("logs").EnumerateArray())
        {
            Assert.Equal("register", log.GetProperty("event").GetString());
        }
    }

    // -- Dashboard HTML --

    [Fact]
    public async Task Dashboard_ReturnsHtml()
    {
        var response = await _client.GetAsync("/api/admin/dashboard");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Arrow Thing", body);
        Assert.Contains("Admin", body);
    }
}
