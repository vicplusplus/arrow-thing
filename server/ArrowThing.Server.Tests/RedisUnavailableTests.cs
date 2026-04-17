using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 2 Redis-unavailable behavior. Forces the DI container to resolve
/// <see cref="IConnectionMultiplexer"/> as <c>null</c> so surfaces that gate
/// on <c>RedisExtensions.IsAvailable</c> must return 503 instead of throwing.
///
/// The factory still needs Postgres (auth flows persist users), so we inherit
/// from <see cref="TestFactory"/> and swap only the Redis registration.
/// </summary>
public class RedisUnavailableTests
    : IClassFixture<RedisUnavailableTests.NoRedisFactory>,
        IDisposable
{
    private readonly NoRedisFactory _factory;
    private readonly HttpClient _client;
    private readonly string _deviceId = Guid.NewGuid().ToString("N");

    public RedisUnavailableTests(NoRedisFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", _deviceId);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task SubmitScore_RedisDown_Returns503()
    {
        var token = await RegisterAndGetTokenAsync("redis-down-score@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Minimal payload — we only need to reach the Redis-availability gate,
        // which sits after pre-verification. Pre-verify will reject this junk
        // with a 400 long before Redis is checked, so use a real replay shape.
        var replayJson = ReliabilityReplayFactory.MakeMinimalValidReplay();
        using var response = await _client.PostAsJsonAsync("/api/scores", new { replayJson });

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
    }

    [Fact]
    public async Task ScoreStatus_RedisDown_Returns503()
    {
        var token = await RegisterAndGetTokenAsync("redis-down-status@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        using var response = await _client.GetAsync($"/api/scores/{Guid.NewGuid()}/status");
        Assert.Equal((HttpStatusCode)503, response.StatusCode);
    }

    [Fact]
    public async Task CreateLobby_RedisDown_Returns503()
    {
        var token = await RegisterAndGetTokenAsync("redis-down-lobby@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        using var response = await _client.PostAsJsonAsync(
            "/api/lobbies",
            new { name = "Test Lobby" }
        );

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
    }

    [Fact]
    public async Task Health_RedisDown_StillReturns200()
    {
        // The health endpoint must not depend on Redis — it's used by the
        // load balancer / Docker healthcheck to decide if the container is up.
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Leaderboard_RedisDown_StillServedFromDb()
    {
        // Leaderboard reads fall back to the database when the Redis cache is
        // unavailable. This endpoint is part of the read path we explicitly
        // want to keep alive during a Redis outage.
        using var response = await _client.GetAsync("/api/leaderboards/10x10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> RegisterAndGetTokenAsync(string email)
    {
        // Auth flows don't need Redis — they use Postgres only.
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "Password123!",
                displayName = "NoRedis",
            }
        );
        var code = _factory.FakeEmail.SentEmails.FindLast(e =>
            e.To == email && e.Type == "verification"
        );
        var verify = await _client.PostAsJsonAsync(
            "/api/auth/verify-code",
            new { email, code = code!.Token }
        );
        var body = await verify.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    /// <summary>
    /// Standalone factory that brings up Postgres but leaves Redis unconfigured
    /// so <c>IConnectionMultiplexer</c> resolves to null. Not a subclass of
    /// <see cref="TestFactory"/> because xUnit's <c>IAsyncLifetime</c> plays
    /// poorly with inherited WebApplicationFactory subclasses (the base's
    /// TestServer can be disposed before child tests run).
    /// </summary>
    public class NoRedisFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private static readonly string? EnvPgConn = Environment.GetEnvironmentVariable(
            "TEST_PG_CONN"
        );
        private static readonly bool UseLocalServices = !string.IsNullOrEmpty(EnvPgConn);

        private readonly PostgreSqlContainer? _postgres = UseLocalServices
            ? null
            : new PostgreSqlBuilder("postgres:16-alpine").Build();

        public FakeEmailService FakeEmail { get; } = new();

        public async Task InitializeAsync()
        {
            if (UseLocalServices)
            {
                await using var conn = new Npgsql.NpgsqlConnection(EnvPgConn!);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "DROP SCHEMA public CASCADE; CREATE SCHEMA public; GRANT ALL ON SCHEMA public TO public;";
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            await _postgres!.StartAsync();
        }

        public new async Task DisposeAsync()
        {
            await base.DisposeAsync();
            if (UseLocalServices)
                return;
            await _postgres!.DisposeAsync();
        }

        private string PgConnectionString() =>
            UseLocalServices ? EnvPgConn! : _postgres!.GetConnectionString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                {
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Default"] = PgConnectionString(),
                            // Redis intentionally unset — the factory registers a null
                            // multiplexer and the RedisExtensions.IsAvailable gate kicks in.
                            ["Redis:ConnectionString"] = "",
                            ["Jwt:Secret"] =
                                "test-secret-that-is-at-least-32-bytes-long-for-hmac256!",
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
                var descriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                );
                if (descriptor != null)
                    services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(options =>
                    options.UseNpgsql(PgConnectionString())
                );

                var emailDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(IEmailService)
                );
                if (emailDescriptor != null)
                    services.Remove(emailDescriptor);
                services.AddSingleton<IEmailService>(fakeEmail);

                // No workers — they require Redis and would crash-loop.
            });
        }
    }
}

internal static class ReliabilityReplayFactory
{
    public static string MakeMinimalValidReplay()
    {
        const int seed = 42;
        const int width = 10;
        const int height = 10;
        const int maxArrowLength = 5;

        var board = new Board(width, height);
        TestBoardHelper.FillBoard(board, maxArrowLength, seed);

        var snapshot = new List<List<Cell>>();
        foreach (var arrow in board.Arrows)
            snapshot.Add(new List<Cell>(arrow.Cells));

        var events = new List<ReplayEvent>();
        int seq = 0;
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.SessionStart,
                timestamp = baseTime.ToString("O"),
            }
        );
        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.StartSolve,
                timestamp = baseTime.AddSeconds(1).ToString("O"),
            }
        );

        double t = 1.5;
        while (board.Arrows.Count > 0)
        {
            Arrow? toClear = null;
            foreach (var arrow in board.Arrows)
            {
                if (board.IsClearable(arrow))
                {
                    toClear = arrow;
                    break;
                }
            }
            events.Add(
                new ReplayEvent
                {
                    seq = seq++,
                    type = ReplayEventType.Clear,
                    posX = toClear!.HeadCell.X,
                    posY = toClear.HeadCell.Y,
                    timestamp = baseTime.AddSeconds(t).ToString("O"),
                }
            );
            board.RemoveArrow(toClear);
            t += 0.5;
        }

        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.EndSolve,
                timestamp = baseTime.AddSeconds(t).ToString("O"),
            }
        );

        var replay = new ReplayData
        {
            version = 4,
            gameId = Guid.NewGuid().ToString(),
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            maxArrowLength = maxArrowLength,
            inspectionDuration = 0f,
            events = events,
            finalTime = t - 1.0,
        };
        replay.SetSnapshotArrows(snapshot);
        return replay.ToJson();
    }
}
