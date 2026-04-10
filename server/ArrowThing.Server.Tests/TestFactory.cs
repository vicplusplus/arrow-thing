using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Games;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ArrowThing.Server.Tests;

public class TestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "postgres:16-alpine"
    ).Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    /// <summary>
    /// Emails captured by the fake EmailService during tests.
    /// </summary>
    public FakeEmailService FakeEmail { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(
            (_, config) =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = _postgres.GetConnectionString(),
                        ["Redis:ConnectionString"] = _redis.GetConnectionString(),
                        ["Jwt:Secret"] = "test-secret-that-is-at-least-32-bytes-long-for-hmac256!",
                        ["Resend:ApiKey"] = "re_test_fake_key",
                        ["Resend:FromAddress"] = "test@arrow-thing.com",
                        ["Admin:ApiKey"] = "test-admin-key",
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

            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString())
            );

            // Replace IEmailService with fake
            var emailDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IEmailService)
            );
            if (emailDescriptor != null)
                services.Remove(emailDescriptor);

            services.AddSingleton<IEmailService>(fakeEmail);

            // Run verification worker in-process so tests can exercise the full async flow
            services.AddHostedService<VerificationWorker>();
        });
    }
}

public record CapturedEmail(string To, string Token, string Type);

public class FakeEmailService : IEmailService
{
    public List<CapturedEmail> SentEmails { get; } = new();
    public List<string> Notifications { get; } = new();

    public Task SendVerificationCodeAsync(string toEmail, string code)
    {
        SentEmails.Add(new CapturedEmail(toEmail, code, "verification"));
        return Task.CompletedTask;
    }

    public Task SendAlreadyRegisteredEmailAsync(string toEmail)
    {
        SentEmails.Add(new CapturedEmail(toEmail, "", "already-registered"));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(string toEmail, string code)
    {
        SentEmails.Add(new CapturedEmail(toEmail, code, "reset"));
        return Task.CompletedTask;
    }

    public Task SendEmailChangeCodeAsync(string toNewEmail, string code)
    {
        SentEmails.Add(new CapturedEmail(toNewEmail, code, "email-change"));
        return Task.CompletedTask;
    }

    public Task SendEmailChangeNotificationAsync(string toOldEmail, string newEmail)
    {
        Notifications.Add(toOldEmail);
        return Task.CompletedTask;
    }
}
