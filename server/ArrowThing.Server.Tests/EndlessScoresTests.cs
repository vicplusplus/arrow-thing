using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

/// <summary>
/// HTTP-level tests for the endless score-submission pipeline. Mirrors
/// <see cref="ScoresTests"/> for classic — same WebApplicationFactory shape,
/// same auth helper, same submit-and-poll loop against the verification
/// worker (which runs in-process under <see cref="TestFactory"/>). Asserts
/// on response status codes plus DB rows in <c>EndlessScores</c>.
/// </summary>
public class EndlessScoresTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public EndlessScoresTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    private async Task<string> RegisterAndGetTokenAsync(
        string email,
        string password = "Password123!",
        string displayName = "EndlessTester"
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
        var auth = await verifyResponse.Content.ReadFromJsonAsync<EndlessAuthResponse>();
        return auth!.Token;
    }

    private async Task<(
        HttpResponseMessage Response,
        EndlessStatusResult? Status
    )> SubmitAndWaitAsync(string replayJson)
    {
        var response = await _client.PostAsJsonAsync("/api/endless-scores", new { replayJson });
        if (response.StatusCode != HttpStatusCode.Accepted)
            return (response, null);

        var accepted = await response.Content.ReadFromJsonAsync<EndlessAcceptedResponse>();
        var gameId = accepted!.GameId;

        // Poll the shared verify:result keyspace via the same status endpoint
        // classic uses (the Program.cs comment notes one endpoint serves
        // both modes).
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            var statusResp = await _client.GetAsync($"/api/scores/{gameId}/status");
            var status = await statusResp.Content.ReadFromJsonAsync<EndlessStatusResult>();
            if (status!.Status != "pending")
                return (response, status);
        }
        throw new TimeoutException(
            $"Endless verification for game {gameId} did not complete in time"
        );
    }

    private async Task<EndlessStatusResult> SubmitAndExpectVerifiedAsync(string replayJson)
    {
        var (response, status) = await SubmitAndWaitAsync(replayJson);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(status);
        Assert.Equal("verified", status!.Status);
        return status;
    }

    /// <summary>
    /// Builds a valid endless replay by driving an <see cref="EndlessRun"/>
    /// to passive topout (no taps). Adapted from
    /// <see cref="EndlessReplayVerifierTests"/> so HTTP tests don't reinvent
    /// the recorder pipeline.
    /// </summary>
    private static string BuildValidEndlessReplayJson(
        int seed = 42,
        int width = 10,
        int height = 10
    )
    {
        var board = new Board(width, height);
        var tuning = EndlessTuning.ForVersion(1);
        var run = new EndlessRun(board, seed, paletteCount: 6, tuning);

        var recorder = new ReplayRecorder();
        recorder.RecordSessionStart();

        const float dt = 0.1f;
        const float maxSimSeconds = 600f;
        float simAcc = 0f;
        while (run.IsActive && simAcc < maxSimSeconds)
        {
            run.Advance(dt);
            simAcc += dt;
        }
        if (run.IsActive)
            throw new InvalidOperationException("Run did not top out within budget.");

        recorder.RecordTopout(run.RunDurationSeconds);

        var replay = new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Endless,
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            tuningsVersion = 1,
            clears = run.ClearCount,
            longestCombo = run.LongestCombo,
            durationSeconds = run.RunDurationSeconds,
            events = new List<ReplayEvent>(recorder.Events),
            finalTime = -1.0,
        };
        return replay.ToJson();
    }

    /// <summary>
    /// Builds a valid endless replay where the player clears at least one
    /// arrow before topping out. Lets tests construct two distinct PBs so
    /// the "more clears wins" rule can be exercised.
    /// </summary>
    private static string BuildActiveEndlessReplayJson(
        int seed,
        int width,
        int height,
        int targetClears
    )
    {
        var board = new Board(width, height);
        var tuning = EndlessTuning.ForVersion(1);
        var run = new EndlessRun(board, seed, paletteCount: 6, tuning);

        var recorder = new ReplayRecorder();
        recorder.RecordSessionStart();

        const float dt = 0.1f;
        const float maxSimSeconds = 600f;
        float simAcc = 0f;
        int clearsDone = 0;
        while (run.IsActive && simAcc < maxSimSeconds)
        {
            run.Advance(dt);
            simAcc += dt;
            if (clearsDone < targetClears && run.IsActive)
            {
                Arrow? clearable = null;
                foreach (var arrow in run.Board.Arrows)
                {
                    if (run.Board.IsClearable(arrow))
                    {
                        clearable = arrow;
                        break;
                    }
                }
                if (clearable != null)
                {
                    var head = clearable.HeadCell;
                    if (run.HandleTap(head.X, head.Y) == TapResult.Cleared)
                    {
                        recorder.RecordClear(head.X, head.Y, run.SimTime);
                        clearsDone++;
                    }
                }
            }
        }
        if (run.IsActive)
            throw new InvalidOperationException("Active run did not top out.");
        recorder.RecordTopout(run.RunDurationSeconds);

        var replay = new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Endless,
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            tuningsVersion = 1,
            clears = run.ClearCount,
            longestCombo = run.LongestCombo,
            durationSeconds = run.RunDurationSeconds,
            events = new List<ReplayEvent>(recorder.Events),
            finalTime = -1.0,
        };
        return replay.ToJson();
    }

    // ---- Tests ------------------------------------------------------------

    [Fact]
    public async Task SubmitValidReplay_LandsInDatabase()
    {
        var token = await RegisterAndGetTokenAsync("end-submit1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var replayJson = BuildValidEndlessReplayJson(seed: 100, width: 10, height: 10);
        var status = await SubmitAndExpectVerifiedAsync(replayJson);

        Assert.True(status.IsPersonalBest == true);
        Assert.True(status.Rank > 0);

        // Confirm DB row.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "end-submit1@test.com");
        var row = await db.EndlessScores.FirstOrDefaultAsync(s => s.UserId == user.Id);
        Assert.NotNull(row);
        Assert.Equal(10, row!.BoardWidth);
        Assert.Equal(10, row.BoardHeight);
    }

    [Fact]
    public async Task SubmitSameGameId_Idempotent()
    {
        var token = await RegisterAndGetTokenAsync("end-idem@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var replayJson = BuildValidEndlessReplayJson(seed: 200, width: 10, height: 10);
        await SubmitAndExpectVerifiedAsync(replayJson);

        // Resubmit identical replay (same gameId) — second submission
        // hits the idempotency path and returns 200 / IsPersonalBest=false.
        var second = await _client.PostAsJsonAsync("/api/endless-scores", new { replayJson });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<EndlessSubmitResult>();
        Assert.True(body!.Verified);
        Assert.False(body.IsPersonalBest);
    }

    [Fact]
    public async Task PB_MoreClearsReplacesExisting()
    {
        var token = await RegisterAndGetTokenAsync("end-pb-clears@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // First submission: passive topout (likely 0 clears).
        var first = BuildValidEndlessReplayJson(seed: 301, width: 10, height: 10);
        await SubmitAndExpectVerifiedAsync(first);

        // Capture current row.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "end-pb-clears@test.com");
        var firstRow = await db.EndlessScores.AsNoTracking().FirstAsync(s => s.UserId == user.Id);

        // Second submission: drive at least 3 clears so VerifiedClears > firstRow.Clears.
        var second = BuildActiveEndlessReplayJson(
            seed: 302,
            width: 10,
            height: 10,
            targetClears: 3
        );
        var status2 = await SubmitAndExpectVerifiedAsync(second);
        Assert.True(status2.IsPersonalBest == true);

        // DB row should be replaced (still single row per user/board, with higher Clears).
        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db2
            .EndlessScores.Where(s =>
                s.UserId == user.Id && s.BoardWidth == 10 && s.BoardHeight == 10
            )
            .ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].Clears > firstRow.Clears);
    }

    [Fact]
    public async Task PB_RejectsLowerClears()
    {
        var token = await RegisterAndGetTokenAsync("end-pb-worse@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // First: a richer run with multiple clears.
        var first = BuildActiveEndlessReplayJson(seed: 401, width: 10, height: 10, targetClears: 4);
        await SubmitAndExpectVerifiedAsync(first);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "end-pb-worse@test.com");
        var beforeClears = (
            await db.EndlessScores.AsNoTracking().FirstAsync(s => s.UserId == user.Id)
        ).Clears;

        // Second: passive topout (0 clears) — strictly worse, should be rejected as PB.
        var second = BuildValidEndlessReplayJson(seed: 402, width: 10, height: 10);
        var status2 = await SubmitAndExpectVerifiedAsync(second);
        Assert.False(status2.IsPersonalBest);

        // DB still has the original (better) score.
        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var afterClears = (
            await db2.EndlessScores.AsNoTracking().FirstAsync(s => s.UserId == user.Id)
        ).Clears;
        Assert.Equal(beforeClears, afterClears);
    }

    [Fact]
    public async Task PB_EqualClearsShorterDurationReplaces()
    {
        // The PB rule is: more clears OR equal clears + shorter duration.
        // Direct unit test of the worker's improvement decision: build a
        // first row with N clears + duration D1, then post a replay with
        // N clears + duration D2 < D1, and assert replacement.
        //
        // Easiest way to construct this: insert the "first" row directly
        // into the DB with a deliberately long duration, then submit the
        // newly-built replay (which will have duration < the inserted one).
        var token = await RegisterAndGetTokenAsync("end-pb-tie@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var first = BuildValidEndlessReplayJson(seed: 501, width: 10, height: 10);
        var firstStatus = await SubmitAndExpectVerifiedAsync(first);
        Assert.True(firstStatus.IsPersonalBest);

        // Bump the existing row's duration so it's strictly worse than any
        // re-submission. Keep clears identical.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == "end-pb-tie@test.com");
            var row = await db.EndlessScores.FirstAsync(s => s.UserId == user.Id);
            row.DurationSeconds = row.DurationSeconds + 100.0; // dramatically worse
            await db.SaveChangesAsync();
        }

        // Post a different run with the same clears (likely 0) but a
        // shorter duration than the bumped value. Worker should treat
        // this as a PB.
        var second = BuildValidEndlessReplayJson(seed: 502, width: 10, height: 10);
        var status2 = await SubmitAndExpectVerifiedAsync(second);
        // The new row likely has equal clears (both passive runs) and a
        // duration ~original < (original + 100), so PB=true.
        Assert.True(status2.IsPersonalBest == true);
    }

    [Fact]
    public async Task SubmitClassicMode_Returns400()
    {
        var token = await RegisterAndGetTokenAsync("end-mode@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var replay = new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Classic,
            boardWidth = 10,
            boardHeight = 10,
            tuningsVersion = 1,
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    seq = 0,
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00Z",
                },
            },
            finalTime = -1.0,
        };

        var resp = await _client.PostAsJsonAsync(
            "/api/endless-scores",
            new { replayJson = replay.ToJson() }
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SubmitWithoutAuth_Returns401()
    {
        var replayJson = BuildValidEndlessReplayJson(seed: 600);
        var resp = await _client.PostAsJsonAsync("/api/endless-scores", new { replayJson });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task SubmitMalformedJson_Returns400()
    {
        var token = await RegisterAndGetTokenAsync("end-malformed@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var resp = await _client.PostAsJsonAsync(
            "/api/endless-scores",
            new { replayJson = "not json" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task FlaggedAccount_RejectsSubmission()
    {
        var token = await RegisterAndGetTokenAsync("end-flagged@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Flag the account directly so we can isolate the 403 path from
        // the pre-verify-flagging path.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == "end-flagged@test.com");
            user.Flagged = true;
            user.FlagReason = "manual-test";
            user.FlaggedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var replayJson = BuildValidEndlessReplayJson(seed: 700);
        var resp = await _client.PostAsJsonAsync("/api/endless-scores", new { replayJson });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task PreVerifyFailure_FlagsUser()
    {
        var token = await RegisterAndGetTokenAsync("end-preverify@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Negative claimed clears trips the PreVerify gate, which sets
        // user.Flagged and returns 403.
        var bogus = new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Endless,
            seed = 0,
            boardWidth = 10,
            boardHeight = 10,
            tuningsVersion = 1,
            clears = -5,
            longestCombo = 0,
            durationSeconds = 1f,
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    seq = 0,
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00Z",
                },
            },
            finalTime = -1.0,
        };

        var resp = await _client.PostAsJsonAsync(
            "/api/endless-scores",
            new { replayJson = bogus.ToJson() }
        );
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == "end-preverify@test.com");
        Assert.True(user.Flagged);
        Assert.NotNull(user.FlaggedAt);
        Assert.Contains("endless_pre_verify", user.FlagReason ?? "");
    }
}

internal record EndlessAuthResponse(string Token, string DisplayName);

internal record EndlessAcceptedResponse(string GameId, string Status);

internal class EndlessStatusResult
{
    public string Status { get; set; } = "";
    public int? Rank { get; set; }
    public bool? IsPersonalBest { get; set; }
    public string? Reason { get; set; }
}

internal class EndlessSubmitResult
{
    public bool Verified { get; set; }
    public int? Rank { get; set; }
    public bool IsPersonalBest { get; set; }
    public string? Reason { get; set; }
}
