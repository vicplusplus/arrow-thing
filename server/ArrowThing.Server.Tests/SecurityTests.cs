using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Games;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ArrowThing.Server.Tests;

// Pure unit tests — no database or Redis. Kept separate so they run without
// Testcontainers / Docker (e.g. in minimal sandboxes).
public class StartupSecretValidatorTests
{
    [Fact]
    public void Empty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupSecretValidator.ValidateProductionSecret(
                null,
                "Jwt:Secret",
                minLength: 32,
                blocklist: Array.Empty<string>()
            )
        );
        Assert.Contains("must be set", ex.Message);
    }

    [Fact]
    public void TooShort_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupSecretValidator.ValidateProductionSecret(
                "short",
                "Jwt:Secret",
                minLength: 32,
                blocklist: Array.Empty<string>()
            )
        );
        Assert.Contains("at least 32", ex.Message);
    }

    [Fact]
    public void DevDefault_Throws()
    {
        const string devDefault = "local-dev-jwt-secret-not-for-production";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupSecretValidator.ValidateProductionSecret(
                devDefault,
                "Jwt:Secret",
                minLength: 32,
                blocklist: new[] { devDefault }
            )
        );
        Assert.Contains("dev default", ex.Message);
    }

    [Fact]
    public void ValidSecret_Passes()
    {
        StartupSecretValidator.ValidateProductionSecret(
            "a-strong-production-secret-with-plenty-of-entropy",
            "Jwt:Secret",
            minLength: 32,
            blocklist: new[] { "some-dev-default" }
        );
    }
}

public class SecurityTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public SecurityTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    // -- Admin auth policy --

    [Fact]
    public async Task AdminEndpoint_NoKey_Returns401()
    {
        var response = await _client.GetAsync("/api/admin/flagged-users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_WrongKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/flagged-users");
        request.Headers.Add(AdminKeyAuthenticationHandler.HeaderName, "wrong-key");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_CorrectKey_Returns200()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/flagged-users");
        request.Headers.Add(AdminKeyAuthenticationHandler.HeaderName, "test-admin-key");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -- Score submission idempotency --

    [Fact]
    public async Task SubmitScore_DuplicateGameIdWhileLockHeld_DoesNotDoubleEnqueue()
    {
        // Arrange: register a user and claim the Redis verification lock for a fake gameId
        // to simulate an in-flight verification. A second submission with the same gameId
        // must return 202 (idempotent) without pushing a new job onto the queue.
        var token = await RegisterAndGetTokenAsync("idempotency@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var db = redis.GetDatabase();

        // Drain any pending queue entries so length deltas are meaningful.
        await db.KeyDeleteAsync(GameService.StandardQueueKey);
        await db.KeyDeleteAsync(GameService.HeavyQueueKey);

        var gameId = Guid.NewGuid();
        var lockKey = $"{GameService.LockKeyPrefix}{gameId}";
        await db.KeyDeleteAsync(lockKey);

        // Pre-claim the lock with a different "user" to simulate an in-flight job.
        await db.StringSetAsync(
            lockKey,
            Guid.NewGuid().ToString(),
            TimeSpan.FromMinutes(5),
            when: When.NotExists
        );

        var replayJson = MakeReplayJsonWithGameId(gameId);

        var before = await db.ListLengthAsync(GameService.StandardQueueKey);

        var response = await _client.PostAsJsonAsync("/api/scores", new { replayJson });

        var after = await db.ListLengthAsync(GameService.StandardQueueKey);

        // Accepted + no queue growth proves the lock short-circuit worked.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(before, after);

        // Cleanup so the worker doesn't see a stale lock across tests.
        await db.KeyDeleteAsync(lockKey);
    }

    // -- Email-change race --

    [Fact]
    public async Task ConfirmEmailChange_ConcurrentToSameEmail_OneSucceedsOneReturns409()
    {
        const string shared = "shared-race@example.com";

        var tokenA = await RegisterAndGetTokenAsync("racer-a@example.com");
        var tokenB = await RegisterAndGetTokenAsync("racer-b@example.com");

        // Both users request a change to the same new email. Neither has committed
        // yet, so both ChangeEmail calls pass the existence pre-check.
        await RequestEmailChangeAsync(tokenA, shared);
        await RequestEmailChangeAsync(tokenB, shared);

        var codes = _factory
            .FakeEmail.SentEmails.Where(e => e.To == shared && e.Type == "email-change")
            .ToList();
        Assert.Equal(2, codes.Count);

        // Fire both confirms concurrently — under load the DB unique index on
        // Users.Email is the authoritative guard. One must succeed, the other
        // must be rejected with 409.
        var taskA = ConfirmEmailChangeAsync(tokenA, shared, codes[0].Token);
        var taskB = ConfirmEmailChangeAsync(tokenB, shared, codes[1].Token);
        var responses = await Task.WhenAll(taskA, taskB);

        var statuses = responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);
    }

    private async Task RequestEmailChangeAsync(string token, string newEmail)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-email")
        {
            Content = JsonContent.Create(new { newEmail, currentPassword = "Password123!" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(req);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> ConfirmEmailChangeAsync(
        string token,
        string email,
        string code
    )
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/confirm-email-change")
        {
            Content = JsonContent.Create(new { email, code }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(req);
    }

    // -- Helpers --

    private async Task<string> RegisterAndGetTokenAsync(string email)
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "Password123!",
                displayName = "Sec User",
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
        return auth!.Token;
    }

    private static string MakeReplayJsonWithGameId(Guid gameId)
    {
        // Minimal valid-shape replay for a 10x10 board. Pre-verification only checks
        // shape and static invariants, which is all this test exercises.
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
            gameId = gameId.ToString(),
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
