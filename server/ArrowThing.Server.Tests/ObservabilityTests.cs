using System.Net;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

public class ObservabilityTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public ObservabilityTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    // -- Metrics Endpoint --

    [Fact]
    public async Task Metrics_ReturnsPrometheusFormat()
    {
        var response = await _client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE", content);
    }

    // -- Audit Logging --

    [Fact]
    public async Task Register_CreatesAuditLogEntry()
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = "audit-register@example.com",
                password = "password123",
                displayName = "Audit Register",
            }
        );

        var logs = await GetAuditLogs("audit-register@example.com");
        Assert.Contains(logs, l => l.Event == AuditEvent.Register);
    }

    [Fact]
    public async Task Login_Success_CreatesAuditLogEntry()
    {
        await RegisterAndVerifyAsync("audit-login@example.com", "password123", "Audit Login");

        await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "audit-login@example.com", password = "password123" }
        );

        var logs = await GetAuditLogs("audit-login@example.com");
        Assert.Contains(logs, l => l.Event == AuditEvent.Login);
    }

    [Fact]
    public async Task Login_Failure_CreatesAuditLogEntry()
    {
        await RegisterAndVerifyAsync("audit-fail@example.com", "password123", "Audit Fail");

        await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "audit-fail@example.com", password = "wrongpassword" }
        );

        var logs = await GetAuditLogs("audit-fail@example.com");
        Assert.Contains(logs, l => l.Event == AuditEvent.LoginFailed);
    }

    [Fact]
    public async Task LockAccount_CreatesAuditLogEntry()
    {
        await RegisterAndVerifyAsync("audit-lock@example.com", "password123", "Audit Lock");

        var lockRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/lock-account")
        {
            Content = JsonContent.Create(new { email = "audit-lock@example.com" }),
        };
        lockRequest.Headers.Add("X-Admin-Key", "test-admin-key");
        await _client.SendAsync(lockRequest);

        var logs = await GetAuditLogs("audit-lock@example.com");
        Assert.Contains(logs, l => l.Event == AuditEvent.LockAccount);
    }

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

    private async Task<List<AuditLog>> GetAuditLogs(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.AuditLogs.Where(l => l.Email == email).ToList();
    }
}
