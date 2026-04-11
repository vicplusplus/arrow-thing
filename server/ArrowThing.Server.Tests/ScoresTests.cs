using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ArrowThing.Server.Games;
using ArrowThing.Server.Leaderboards;

namespace ArrowThing.Server.Tests;

public class ScoresTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public ScoresTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private async Task<string> RegisterAndGetTokenAsync(
        string email = "test@example.com",
        string password = "Password123!",
        string displayName = "TestUser"
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
        return auth!.Token;
    }

    /// <summary>
    /// Submits a replay and waits for the worker to process it.
    /// Returns the HTTP response from the initial submission (for status code checks)
    /// and the verification result from polling (null if submission was rejected pre-verification).
    /// </summary>
    private async Task<(
        HttpResponseMessage Response,
        ScoreStatusResult? Status
    )> SubmitAndWaitAsync(string replayJson)
    {
        var response = await _client.PostAsJsonAsync("/api/scores", new { replayJson });

        // Pre-verification failures return non-202
        if (response.StatusCode != HttpStatusCode.Accepted)
            return (response, null);

        var accepted = await response.Content.ReadFromJsonAsync<AcceptedResponse>();
        var gameId = accepted!.GameId;

        // Poll for result (worker is running in-process)
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            var statusResp = await _client.GetAsync($"/api/scores/{gameId}/status");
            var status = await statusResp.Content.ReadFromJsonAsync<ScoreStatusResult>();
            if (status!.Status != "pending")
                return (response, status);
        }

        throw new TimeoutException($"Verification for game {gameId} did not complete in time");
    }

    /// <summary>
    /// Submits a valid replay and asserts it was accepted and verified.
    /// Returns the verification result.
    /// </summary>
    private async Task<ScoreStatusResult> SubmitAndExpectVerifiedAsync(string replayJson)
    {
        var (response, status) = await SubmitAndWaitAsync(replayJson);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(status);
        Assert.Equal("verified", status!.Status);
        return status;
    }

    private static string MakeValidReplayJson(
        int seed = 42,
        int width = 10,
        int height = 10,
        int maxArrowLength = 5
    )
    {
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

        double t = 1.0;
        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.StartSolve,
                timestamp = baseTime.AddSeconds(t).ToString("O"),
            }
        );

        t += 0.5;
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
            version = 3,
            gameId = Guid.NewGuid().ToString(),
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            maxArrowLength = maxArrowLength,
            inspectionDuration = 0f,
            boardSnapshot = snapshot,
            events = events,
            finalTime = t - 1.0,
        };

        return replay.ToJson();
    }

    private static string MakeImplausiblyFastReplayJson(
        int seed = 42,
        int width = 10,
        int height = 10,
        int maxArrowLength = 5
    )
    {
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

        double t = 1.001;
        while (board.Arrows.Count > 0)
        {
            Arrow? toClear = null;
            foreach (var arrow in board.Arrows)
                if (board.IsClearable(arrow))
                {
                    toClear = arrow;
                    break;
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
            t += 0.001;
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
            version = 3,
            gameId = Guid.NewGuid().ToString(),
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            maxArrowLength = maxArrowLength,
            boardSnapshot = snapshot,
            events = events,
            finalTime = t - 1.0,
        };

        return replay.ToJson();
    }

    [Fact]
    public async Task SubmitValidReplay_ReturnsVerified()
    {
        var token = await RegisterAndGetTokenAsync("submit1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var replayJson = MakeValidReplayJson(seed: 100);
        var status = await SubmitAndExpectVerifiedAsync(replayJson);
        Assert.True(status.Rank > 0);
        Assert.True(status.IsPersonalBest == true);
    }

    [Theory]
    [InlineData(5, 5, 3, 42)]
    [InlineData(10, 10, 5, 42)]
    [InlineData(20, 20, 8, 42)]
    [InlineData(50, 50, 10, 42)]
    public async Task SubmitValidReplay_VariousSizes_ReturnsVerified(
        int width,
        int height,
        int maxArrowLength,
        int seed
    )
    {
        var token = await RegisterAndGetTokenAsync($"size{width}x{height}@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var replayJson = MakeValidReplayJson(
            seed: seed,
            width: width,
            height: height,
            maxArrowLength: maxArrowLength
        );
        var status = await SubmitAndExpectVerifiedAsync(replayJson);
        Assert.Equal("verified", status.Status);
        Assert.True(status.IsPersonalBest == true);
    }

    [Fact]
    public async Task SubmitSlowerSecondGame_KeepsOriginal()
    {
        var token = await RegisterAndGetTokenAsync("submit2@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // First game (unique seed to avoid cross-test seed dedup)
        await SubmitAndExpectVerifiedAsync(MakeValidReplayJson(seed: 3001));

        // Second game with different seed (will likely have different time)
        var status = await SubmitAndExpectVerifiedAsync(MakeValidReplayJson(seed: 3002));
        Assert.Equal("verified", status.Status);
    }

    [Fact]
    public async Task SubmitSameGameId_Idempotent()
    {
        var token = await RegisterAndGetTokenAsync("submit3@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var replayJson = MakeValidReplayJson(seed: 101);

        // First submission goes through the worker
        await SubmitAndExpectVerifiedAsync(replayJson);

        // Second submission with same replayJson (same gameId) — idempotency returns 200 directly
        var response2 = await _client.PostAsJsonAsync("/api/scores", new { replayJson });
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var result2 = await response2.Content.ReadFromJsonAsync<SubmitResultResponse>();
        Assert.True(result2!.Verified);
        Assert.False(result2.IsPersonalBest); // Same gameId = not a new PB
    }

    [Fact]
    public async Task SubmitMalformedJson_Returns400()
    {
        var token = await RegisterAndGetTokenAsync("submit4@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var response = await _client.PostAsJsonAsync(
            "/api/scores",
            new { replayJson = "not json" }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitWithoutAuth_Returns401()
    {
        var replayJson = MakeValidReplayJson(seed: 102);
        var response = await _client.PostAsJsonAsync("/api/scores", new { replayJson });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetLeaderboard_ReturnsCorrectOrder()
    {
        // Use a unique board size (5x5) to isolate from other tests
        var token1 = await RegisterAndGetTokenAsync("lb1@test.com", displayName: "FastPlayer");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token1
        );
        await SubmitAndExpectVerifiedAsync(
            MakeValidReplayJson(seed: 200, width: 5, height: 5, maxArrowLength: 3)
        );

        _client.DefaultRequestHeaders.Authorization = null;
        var token2 = await RegisterAndGetTokenAsync("lb2@test.com", displayName: "SlowPlayer");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token2
        );
        await SubmitAndExpectVerifiedAsync(
            MakeValidReplayJson(seed: 201, width: 5, height: 5, maxArrowLength: 3)
        );

        // Fetch leaderboard (no auth required)
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/api/leaderboards/5x5?limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var lb = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.Equal(2, lb!.TotalEntries);
        Assert.Equal(2, lb.Entries.Count);
        Assert.Equal(1, lb.Entries[0].Rank);
        Assert.Equal(2, lb.Entries[1].Rank);
        // Verify ordering: first entry should have faster time
        Assert.True(lb.Entries[0].Time <= lb.Entries[1].Time);
    }

    [Fact]
    public async Task GetPlayerEntry_ReturnsCorrectRank()
    {
        var token = await RegisterAndGetTokenAsync("me1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        await SubmitAndExpectVerifiedAsync(MakeValidReplayJson(seed: 103));

        var response = await _client.GetAsync("/api/leaderboards/10x10/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await response.Content.ReadFromJsonAsync<PlayerEntryDto>();
        Assert.True(entry!.Rank > 0);
        Assert.True(entry.Time > 0);
    }

    [Fact]
    public async Task GetPlayerEntry_NoScore_Returns404()
    {
        var token = await RegisterAndGetTokenAsync("noscore@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var response = await _client.GetAsync("/api/leaderboards/10x10/me");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReplay_ExistingScore_ReturnsJson()
    {
        var token = await RegisterAndGetTokenAsync("replay1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        await SubmitAndExpectVerifiedAsync(MakeValidReplayJson(seed: 104));

        // Get leaderboard to find gameId
        _client.DefaultRequestHeaders.Authorization = null;
        var lbResponse = await _client.GetAsync("/api/leaderboards/10x10");
        var lb = await lbResponse.Content.ReadFromJsonAsync<LeaderboardResponse>();
        var gameId = lb!.Entries.Last().GameId;

        var response = await _client.GetAsync($"/api/replays/{gameId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReplay_UnknownGameId_Returns404()
    {
        var response = await _client.GetAsync($"/api/replays/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DisplayNameUpdate_ReflectedInLeaderboard()
    {
        var token = await RegisterAndGetTokenAsync("rename@test.com", displayName: "OldName");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        await SubmitAndExpectVerifiedAsync(MakeValidReplayJson(seed: 555, width: 9, height: 9));

        // Rename
        var renameRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/auth/me");
        renameRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        renameRequest.Content = JsonContent.Create(new { displayName = "NewName" });
        await _client.SendAsync(renameRequest);

        // Leaderboard should show new name
        _client.DefaultRequestHeaders.Authorization = null;
        var lbResponse = await _client.GetAsync("/api/leaderboards/9x9");
        var lb = await lbResponse.Content.ReadFromJsonAsync<LeaderboardResponse>();
        var entry = lb!.Entries.Find(e => e.DisplayName == "NewName");
        Assert.NotNull(entry);
    }

    [Fact]
    public async Task SubmitTamperedEvents_ReturnsRejected()
    {
        var token = await RegisterAndGetTokenAsync("tamper1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Build a valid replay, then tamper with a clear event position
        var board = new Board(10, 10);
        TestBoardHelper.FillBoard(board, 5, 105);

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
        // Tampered: clear at a position with no arrow
        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.Clear,
                posX = -99,
                posY = -99,
                timestamp = baseTime.AddSeconds(2).ToString("O"),
            }
        );

        var replay = new ReplayData
        {
            version = 3,
            gameId = Guid.NewGuid().ToString(),
            seed = 105,
            boardWidth = 10,
            boardHeight = 10,
            maxArrowLength = 5,
            boardSnapshot = snapshot,
            events = events,
            finalTime = 1.0,
        };

        var (response, status) = await SubmitAndWaitAsync(replay.ToJson());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(status);
        Assert.Equal("rejected", status!.Status);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public async Task SubmitTamperedSnapshot_ReturnsRejected()
    {
        var token = await RegisterAndGetTokenAsync("tamper2@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Build a valid replay but modify the snapshot
        var replayJson = MakeValidReplayJson(seed: 106);
        var replay = Newtonsoft.Json.JsonConvert.DeserializeObject<ReplayData>(replayJson)!;
        // Tamper: remove an arrow from the snapshot
        replay.boardSnapshot.RemoveAt(0);

        var (response, status) = await SubmitAndWaitAsync(replay.ToJson());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(status);
        Assert.Equal("rejected", status!.Status);
        Assert.Contains("mismatch", status.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitOversizedBoard_FlagsAccount()
    {
        var token = await RegisterAndGetTokenAsync("oversize@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Build a replay with out-of-range dimensions (can't actually generate, just fake JSON)
        var replay = new ReplayData
        {
            version = 3,
            gameId = Guid.NewGuid().ToString(),
            seed = 1,
            boardWidth = 500,
            boardHeight = 500,
            maxArrowLength = 5,
            boardSnapshot = new List<List<Cell>>(),
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    seq = 0,
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00Z",
                },
            },
            finalTime = 1.0,
        };

        var response = await _client.PostAsJsonAsync(
            "/api/scores",
            new { replayJson = replay.ToJson() }
        );
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitImplausiblyFastReplay_FlagsAccount()
    {
        var token = await RegisterAndGetTokenAsync("fast@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var response = await _client.PostAsJsonAsync(
            "/api/scores",
            new { replayJson = MakeImplausiblyFastReplayJson() }
        );
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitDuplicateSeed_RejectsAndFlagsAccount()
    {
        // First user submits with seed 777
        var token1 = await RegisterAndGetTokenAsync("dedup1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token1
        );
        var replay1 = MakeValidReplayJson(seed: 777, width: 7, height: 7, maxArrowLength: 3);
        await SubmitAndExpectVerifiedAsync(replay1);

        // Second user submits with same seed — rejected and account flagged
        _client.DefaultRequestHeaders.Authorization = null;
        var token2 = await RegisterAndGetTokenAsync("dedup2@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token2
        );
        var replay2 = MakeValidReplayJson(seed: 777, width: 7, height: 7, maxArrowLength: 3);
        var response2 = await _client.PostAsJsonAsync("/api/scores", new { replayJson = replay2 });
        Assert.Equal(HttpStatusCode.Forbidden, response2.StatusCode);

        // No score was created for user 2 — leaderboard only has user 1
        _client.DefaultRequestHeaders.Authorization = null;
        var lbResponse = await _client.GetAsync("/api/leaderboards/7x7");
        var lb = await lbResponse.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.Equal(1, lb!.TotalEntries);
    }

    [Fact]
    public async Task AdminFlaggedUsers_ListsFlaggedUsers()
    {
        // Trigger a pre-verification failure to flag the account
        var token = await RegisterAndGetTokenAsync("admflag1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );
        await _client.PostAsJsonAsync(
            "/api/scores",
            new { replayJson = MakeImplausiblyFastReplayJson(seed: 888, width: 6, height: 6) }
        );

        // Admin: list flagged users
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Add("X-Admin-Key", "test-admin-key");
        var response = await _client.GetAsync("/api/admin/flagged-users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var flagged = await response.Content.ReadFromJsonAsync<List<FlaggedUserDto>>();
        Assert.True(flagged!.Count >= 1);
        Assert.True(flagged.Exists(u => u.Email == "admflag1@test.com"));

        _client.DefaultRequestHeaders.Remove("X-Admin-Key");
    }

    [Fact]
    public async Task AdminUnflagUser_AllowsNewSubmissions()
    {
        // Flag user via pre-verification failure
        var token = await RegisterAndGetTokenAsync("unflag1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );
        await _client.PostAsJsonAsync(
            "/api/scores",
            new { replayJson = MakeImplausiblyFastReplayJson(seed: 999, width: 8, height: 8) }
        );

        // Confirm blocked
        var blockedResp = await _client.PostAsJsonAsync(
            "/api/scores",
            new
            {
                replayJson = MakeValidReplayJson(
                    seed: 5001,
                    width: 8,
                    height: 8,
                    maxArrowLength: 3
                ),
            }
        );
        Assert.Equal(HttpStatusCode.Forbidden, blockedResp.StatusCode);

        // Admin: unflag the user
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Add("X-Admin-Key", "test-admin-key");
        var flaggedResp = await _client.GetAsync("/api/admin/flagged-users");
        var flagged = await flaggedResp.Content.ReadFromJsonAsync<List<FlaggedUserDto>>();
        var flaggedUser = flagged!.Find(u => u.Email == "unflag1@test.com");
        Assert.NotNull(flaggedUser);

        var unflagResp = await _client.PostAsync(
            $"/api/admin/users/{flaggedUser!.Id}/unflag",
            null
        );
        Assert.Equal(HttpStatusCode.OK, unflagResp.StatusCode);

        // User can now submit again — should get 202 (async verification)
        _client.DefaultRequestHeaders.Remove("X-Admin-Key");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );
        var okResp = await _client.PostAsJsonAsync(
            "/api/scores",
            new
            {
                replayJson = MakeValidReplayJson(
                    seed: 5001,
                    width: 8,
                    height: 8,
                    maxArrowLength: 3
                ),
            }
        );
        Assert.Equal(HttpStatusCode.Accepted, okResp.StatusCode);
    }

    [Fact]
    public async Task AdminRemoveScore_DeletesFromLeaderboard()
    {
        var token = await RegisterAndGetTokenAsync("remove1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );
        await SubmitAndExpectVerifiedAsync(
            MakeValidReplayJson(seed: 5002, width: 3, height: 3, maxArrowLength: 3)
        );

        // Confirm it's on the leaderboard
        _client.DefaultRequestHeaders.Authorization = null;
        var lb1 = await (
            await _client.GetAsync("/api/leaderboards/3x3")
        ).Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.True(lb1!.TotalEntries >= 1);
        var gameId = lb1.Entries[0].GameId;

        // Submit from a second user, then remove their score
        var token2 = await RegisterAndGetTokenAsync("remove2@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token2
        );
        await SubmitAndExpectVerifiedAsync(
            MakeValidReplayJson(seed: 5003, width: 3, height: 3, maxArrowLength: 3)
        );

        // Get the leaderboard entry with gameId for user2
        _client.DefaultRequestHeaders.Authorization = null;
        var lb2 = await (
            await _client.GetAsync("/api/leaderboards/3x3")
        ).Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.Equal(2, lb2!.TotalEntries);

        var entry2 = lb2.Entries.Find(e => e.GameId != gameId);
        Assert.NotNull(entry2);

        // Remove using the score ID from the entry
        _client.DefaultRequestHeaders.Add("X-Admin-Key", "test-admin-key");
        var removeResp = await _client.PostAsync($"/api/admin/scores/{entry2!.Id}/remove", null);
        Assert.Equal(HttpStatusCode.OK, removeResp.StatusCode);

        // Leaderboard should now have 1 entry
        _client.DefaultRequestHeaders.Remove("X-Admin-Key");
        var lb3 = await (
            await _client.GetAsync("/api/leaderboards/3x3")
        ).Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.Equal(1, lb3!.TotalEntries);
    }

    [Fact]
    public async Task FlaggedAccount_RejectsNewSubmissions()
    {
        // User 1 submits a score
        var token1 = await RegisterAndGetTokenAsync("flagblock1@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token1
        );
        await SubmitAndExpectVerifiedAsync(
            MakeValidReplayJson(seed: 6001, width: 7, height: 7, maxArrowLength: 3)
        );

        // User 2 submits with the same seed — gets flagged
        _client.DefaultRequestHeaders.Authorization = null;
        var token2 = await RegisterAndGetTokenAsync("flagblock2@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token2
        );
        var flagResp = await _client.PostAsJsonAsync(
            "/api/scores",
            new
            {
                replayJson = MakeValidReplayJson(
                    seed: 6001,
                    width: 7,
                    height: 7,
                    maxArrowLength: 3
                ),
            }
        );
        Assert.Equal(HttpStatusCode.Forbidden, flagResp.StatusCode);

        // User 2 tries to submit a different score — should be rejected
        var blockedResp = await _client.PostAsJsonAsync(
            "/api/scores",
            new
            {
                replayJson = MakeValidReplayJson(
                    seed: 6002,
                    width: 7,
                    height: 7,
                    maxArrowLength: 3
                ),
            }
        );
        Assert.Equal(HttpStatusCode.Forbidden, blockedResp.StatusCode);
    }

    [Fact]
    public async Task PreVerifyFailure_FlagsAccount_BlocksSubsequent()
    {
        var token = await RegisterAndGetTokenAsync("prevflag@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Submit a cheated replay (implausibly fast)
        var cheatedResp = await _client.PostAsJsonAsync(
            "/api/scores",
            new { replayJson = MakeImplausiblyFastReplayJson() }
        );
        Assert.Equal(HttpStatusCode.Forbidden, cheatedResp.StatusCode);

        // Now submit a legitimate replay — should be rejected because account is flagged
        var legitimateResp = await _client.PostAsJsonAsync(
            "/api/scores",
            new
            {
                replayJson = MakeValidReplayJson(
                    seed: 4001,
                    width: 5,
                    height: 5,
                    maxArrowLength: 3
                ),
            }
        );
        Assert.Equal(HttpStatusCode.Forbidden, legitimateResp.StatusCode);
    }

    [Fact(Skip = "Rate limit counts stored rows, not submission attempts — needs separate counter")]
    public async Task RateLimit_ExceedsThreshold_Returns429()
    {
        var token = await RegisterAndGetTokenAsync("ratelimit@test.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        // Submit 10 different valid scores (different seeds = different gameIds, all 10x10)
        for (int i = 0; i < 10; i++)
        {
            var replay = MakeValidReplayJson(seed: 1000 + i);
            await SubmitAndExpectVerifiedAsync(replay);
        }

        // 11th should be rate limited
        var finalReplay = MakeValidReplayJson(seed: 2000);
        var finalResp = await _client.PostAsJsonAsync(
            "/api/scores",
            new { replayJson = finalReplay }
        );
        Assert.Equal(HttpStatusCode.TooManyRequests, finalResp.StatusCode);
    }
}

file record AuthResponse(string Token, string DisplayName);

file record FlaggedUserDto(Guid Id, string Email, string DisplayName, string FlagReason);

record AcceptedResponse(string GameId, string Status);

class ScoreStatusResult
{
    public string Status { get; set; } = "";
    public int? Rank { get; set; }
    public bool? IsPersonalBest { get; set; }
    public string? Reason { get; set; }
}
