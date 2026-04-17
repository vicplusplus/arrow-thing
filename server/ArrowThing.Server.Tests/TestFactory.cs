using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;
using ArrowThing.Server.Data;
using ArrowThing.Server.Games;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ArrowThing.Server.Tests;

public class TestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string? EnvPgConn = Environment.GetEnvironmentVariable("TEST_PG_CONN");
    private static readonly string? EnvRedisConn = Environment.GetEnvironmentVariable(
        "TEST_REDIS_CONN"
    );
    private static readonly bool UseLocalServices =
        !string.IsNullOrEmpty(EnvPgConn) && !string.IsNullOrEmpty(EnvRedisConn);

    private readonly PostgreSqlContainer? _postgres = UseLocalServices
        ? null
        : new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly RedisContainer? _redis = UseLocalServices
        ? null
        : new RedisBuilder("redis:7-alpine").Build();

    public FakeEmailService FakeEmail { get; } = new();

    public async Task InitializeAsync()
    {
        if (UseLocalServices)
        {
            await using (var conn = new Npgsql.NpgsqlConnection(EnvPgConn!))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "DROP SCHEMA public CASCADE; CREATE SCHEMA public; GRANT ALL ON SCHEMA public TO public;";
                await cmd.ExecuteNonQueryAsync();
            }
            var mux = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(
                EnvRedisConn! + ",allowAdmin=true"
            );
            foreach (var ep in mux.GetEndPoints())
                await mux.GetServer(ep).FlushAllDatabasesAsync();
            await mux.DisposeAsync();
            return;
        }
        await Task.WhenAll(_postgres!.StartAsync(), _redis!.StartAsync());
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (UseLocalServices)
            return;
        await Task.WhenAll(_postgres!.DisposeAsync().AsTask(), _redis!.DisposeAsync().AsTask());
    }

    private string PgConnectionString() =>
        UseLocalServices ? EnvPgConn! : _postgres!.GetConnectionString();

    private string RedisConnectionString() =>
        UseLocalServices ? EnvRedisConn! : _redis!.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(
            (_, config) =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = PgConnectionString(),
                        ["Redis:ConnectionString"] = RedisConnectionString(),
                        ["Jwt:Secret"] = "test-secret-that-is-at-least-32-bytes-long-for-hmac256!",
                        ["Resend:ApiKey"] = "re_test_fake_key",
                        ["Resend:FromAddress"] = "test@arrow-thing.com",
                        ["Admin:ApiKey"] = "test-admin-key",
                        ["Test:EnableCrashEndpoint"] = "true",
                    }
                );
            }
        );

        var fakeEmail = FakeEmail;

        builder.ConfigureServices(services =>
        {
            // Remove the app's DbContext registration
            var descriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>)
            );
            if (descriptor != null)
                services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(PgConnectionString()));

            // Replace IEmailService with fake
            var emailDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IEmailService)
            );
            if (emailDescriptor != null)
                services.Remove(emailDescriptor);

            services.AddSingleton<IEmailService>(fakeEmail);

            // Run verification worker in-process so tests can exercise the full async flow
            services.AddHostedService<VerificationWorker>();

            // Co-op generation worker — also in-process for tests.
            services.AddHostedService<LobbyGenerationWorker>();

            // Override LobbyOptions so generation completes within test timeouts
            // (default is 200x200 which takes ~10s+; tests use 20x20).
            services.Configure<LobbyOptions>(o =>
            {
                o.DefaultWidth = 20;
                o.DefaultHeight = 20;
                o.MaxArrowLength = 10;
            });
        });
    }
}

public record CapturedEmail(string To, string Token, string Type);

public class FakeEmailService : IEmailService
{
    public List<CapturedEmail> SentEmails { get; } = new();
    public List<string> Notifications { get; } = new();

    /// <summary>
    /// When true, every <c>Send*Async</c> call throws to simulate a Resend
    /// outage. Phase 2 tests use this to assert critical paths return 503
    /// instead of silently persisting an undeliverable code.
    /// </summary>
    public bool ThrowOnSend { get; set; }

    private static Task Throw() => throw new InvalidOperationException("simulated email outage");

    public Task SendVerificationCodeAsync(string toEmail, string code)
    {
        if (ThrowOnSend)
            return Throw();
        SentEmails.Add(new CapturedEmail(toEmail, code, "verification"));
        return Task.CompletedTask;
    }

    public Task SendAlreadyRegisteredEmailAsync(string toEmail)
    {
        if (ThrowOnSend)
            return Throw();
        SentEmails.Add(new CapturedEmail(toEmail, "", "already-registered"));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(string toEmail, string code)
    {
        if (ThrowOnSend)
            return Throw();
        SentEmails.Add(new CapturedEmail(toEmail, code, "reset"));
        return Task.CompletedTask;
    }

    public Task SendEmailChangeCodeAsync(string toNewEmail, string code)
    {
        if (ThrowOnSend)
            return Throw();
        SentEmails.Add(new CapturedEmail(toNewEmail, code, "email-change"));
        return Task.CompletedTask;
    }

    public Task SendEmailChangeNotificationAsync(string toOldEmail, string newEmail)
    {
        if (ThrowOnSend)
            return Throw();
        Notifications.Add(toOldEmail);
        return Task.CompletedTask;
    }

    public Task SendDeviceOtpCodeAsync(string toEmail, string code)
    {
        if (ThrowOnSend)
            return Throw();
        SentEmails.Add(new CapturedEmail(toEmail, code, "device-otp"));
        return Task.CompletedTask;
    }
}
