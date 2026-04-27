using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server.Data;
using ArrowThing.Server.Leaderboards;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

/// <summary>
/// HTTP-level tests for the four endless leaderboard endpoints. The
/// submit-and-verify path is exercised in <see cref="EndlessScoresTests"/>;
/// here we plant rows directly via the DB to control ordering and avoid
/// running the worker for every fixture row. Each test seeds a fresh user
/// per row so flagged-exclusion can be tested without cross-contamination
/// from other suites in the shared fixture.
/// </summary>
public class EndlessLeaderboardTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public EndlessLeaderboardTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString("N"));
    }

    public void Dispose() => _client.Dispose();

    private async Task<(Guid UserId, string Token)> RegisterAsync(string email, string displayName)
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "Password123!",
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
        var auth = await verifyResponse.Content.ReadFromJsonAsync<EndlessLbAuthResponse>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        return (user.Id, auth!.Token);
    }

    /// <summary>
    /// Inserts a synthetic <see cref="EndlessScore"/> row directly into the
    /// DB. Skips the worker so tests can plant exact (clears, duration,
    /// board) tuples for ordering / ranking assertions. Also invalidates
    /// the leaderboard cache for the (w, h) key + global so subsequent
    /// reads see the new row.
    /// </summary>
    private async Task<EndlessScore> InsertEndlessScoreAsync(
        Guid userId,
        int width,
        int height,
        int clears,
        double durationSeconds,
        int longestCombo = 0,
        int seed = 0
    )
    {
        EndlessScore row;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            row = new EndlessScore
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                GameId = Guid.NewGuid(),
                Seed = seed,
                BoardWidth = width,
                BoardHeight = height,
                TuningsVersion = 1,
                Clears = clears,
                LongestCombo = longestCombo,
                DurationSeconds = durationSeconds,
                ReplayJson = "",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.EndlessScores.Add(row);
            await db.SaveChangesAsync();

            var cache = scope.ServiceProvider.GetRequiredService<EndlessLeaderboardCache>();
            cache.Invalidate(width, height);
            cache.Invalidate(0, 0); // also drops the "all" cache key
        }
        return row;
    }

    private async Task FlagUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FindAsync(userId);
        user!.Flagged = true;
        user.FlagReason = "test-flag";
        user.FlaggedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var cache = scope.ServiceProvider.GetRequiredService<EndlessLeaderboardCache>();
        cache.InvalidateAll();
    }

    // ---- Per-config top-N -------------------------------------------------

    [Fact]
    public async Task GetTopN_OrdersByClearsDescThenDurationAsc_RanksAssigned()
    {
        // Use a unique board size for isolation from other tests in the
        // shared DB fixture.
        const int W = 13,
            H = 13;

        // Plant 60 rows: random-ish clears values + durations so the sort
        // has to work out the order. We'll then verify the top-50 cap and
        // the ordering rule.
        var rng = new Random(0xBEEF);
        var planted = new List<(int clears, double duration)>();
        for (int i = 0; i < 60; i++)
        {
            var (uid, _) = await RegisterAsync(
                $"end-lb-top-{i}@test.com",
                $"TopUser{i}"
            );
            int clears = rng.Next(0, 30);
            double duration = 5.0 + rng.NextDouble() * 60.0;
            await InsertEndlessScoreAsync(uid, W, H, clears, duration);
            planted.Add((clears, duration));
        }

        var resp = await _client.GetAsync($"/api/endless-leaderboards/{W}x{H}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var lb = await resp.Content.ReadFromJsonAsync<EndlessLbResponse>();

        Assert.Equal(60, lb!.TotalEntries);
        Assert.Equal(50, lb.Entries.Count);

        // Ranks are 1..50.
        for (int i = 0; i < lb.Entries.Count; i++)
            Assert.Equal(i + 1, lb.Entries[i].Rank);

        // Ordering: each entry has clears >= the next one, with duration
        // ascending as the tiebreak when clears tie.
        for (int i = 1; i < lb.Entries.Count; i++)
        {
            var a = lb.Entries[i - 1];
            var b = lb.Entries[i];
            Assert.True(
                a.Clears > b.Clears
                    || (a.Clears == b.Clears && a.DurationSeconds <= b.DurationSeconds),
                $"Ordering violated at index {i}: ({a.Clears}, {a.DurationSeconds}) "
                    + $"-> ({b.Clears}, {b.DurationSeconds})"
            );
        }

        // The top entry is the row with max clears (ties broken by min
        // duration) — match what we planted.
        var expectedTopClears = 0;
        foreach (var (c, _) in planted)
            if (c > expectedTopClears)
                expectedTopClears = c;
        Assert.Equal(expectedTopClears, lb.Entries[0].Clears);
    }

    [Fact]
    public async Task GetTopN_BoardOutOfRange_ReturnsEmpty()
    {
        // The endpoint guard returns an empty response for w<5 / h<5 / w>80 / h>80.
        var tooSmall = await _client.GetAsync("/api/endless-leaderboards/4x4");
        Assert.Equal(HttpStatusCode.OK, tooSmall.StatusCode);
        var smallLb = await tooSmall.Content.ReadFromJsonAsync<EndlessLbResponse>();
        Assert.Equal(0, smallLb!.TotalEntries);
        Assert.Empty(smallLb.Entries);

        var tooBig = await _client.GetAsync("/api/endless-leaderboards/81x81");
        Assert.Equal(HttpStatusCode.OK, tooBig.StatusCode);
        var bigLb = await tooBig.Content.ReadFromJsonAsync<EndlessLbResponse>();
        Assert.Equal(0, bigLb!.TotalEntries);
        Assert.Empty(bigLb.Entries);
    }

    [Fact]
    public async Task GetTopN_ExcludesFlaggedUsers()
    {
        const int W = 14,
            H = 14;

        var (uA, _) = await RegisterAsync("end-lb-flag-a@test.com", "Aaa");
        var (uB, _) = await RegisterAsync("end-lb-flag-b@test.com", "Bbb");
        await InsertEndlessScoreAsync(uA, W, H, clears: 5, durationSeconds: 10.0);
        await InsertEndlessScoreAsync(uB, W, H, clears: 10, durationSeconds: 10.0);

        // Flag B (the better score). Should disappear from the leaderboard.
        await FlagUserAsync(uB);

        var resp = await _client.GetAsync($"/api/endless-leaderboards/{W}x{H}");
        var lb = await resp.Content.ReadFromJsonAsync<EndlessLbResponse>();
        Assert.Equal(1, lb!.TotalEntries);
        Assert.Single(lb.Entries);
        Assert.Equal(5, lb.Entries[0].Clears);
        Assert.Equal("Aaa", lb.Entries[0].DisplayName);
    }

    // ---- /me --------------------------------------------------------------

    [Fact]
    public async Task GetMe_AuthenticatedWithEntry_ReturnsRankAndTotal()
    {
        const int W = 15,
            H = 15;

        // Plant three users; the requesting user is in the middle.
        var (uA, _) = await RegisterAsync("end-lb-me-a@test.com", "MeA");
        var (uMe, meToken) = await RegisterAsync("end-lb-me-self@test.com", "MeSelf");
        var (uC, _) = await RegisterAsync("end-lb-me-c@test.com", "MeC");
        await InsertEndlessScoreAsync(uA, W, H, clears: 50, durationSeconds: 100.0);
        await InsertEndlessScoreAsync(uMe, W, H, clears: 30, durationSeconds: 80.0);
        await InsertEndlessScoreAsync(uC, W, H, clears: 10, durationSeconds: 50.0);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", meToken);
        var resp = await _client.GetAsync($"/api/endless-leaderboards/{W}x{H}/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var entry = await resp.Content.ReadFromJsonAsync<EndlessPlayerEntry>();
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Rank);
        Assert.Equal(3, entry.TotalEntries);
        Assert.Equal(30, entry.Clears);
        Assert.False(entry.Flagged);
    }

    [Fact]
    public async Task GetMe_NoEntry_Returns404()
    {
        var (_, token) = await RegisterAsync("end-lb-me-empty@test.com", "MeEmpty");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.GetAsync("/api/endless-leaderboards/16x16/me");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetMe_Unauthenticated_Returns401()
    {
        // No Authorization header set.
        _client.DefaultRequestHeaders.Authorization = null;
        var resp = await _client.GetAsync("/api/endless-leaderboards/17x17/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- /all -------------------------------------------------------------

    [Fact]
    public async Task GetAll_OrdersByClearsDescDurationAscAreaDesc()
    {
        // Three users on different board configs. Picks the per-user
        // representative (best for that user) and ranks across them by
        // clears desc, duration asc, area desc as final tiebreak.
        var (uA, _) = await RegisterAsync("end-lb-all-a@test.com", "AllA");
        var (uB, _) = await RegisterAsync("end-lb-all-b@test.com", "AllB");
        var (uC, _) = await RegisterAsync("end-lb-all-c@test.com", "AllC");

        // A: 25 clears, 50s, on 18x18 (area 324)
        await InsertEndlessScoreAsync(uA, 18, 18, clears: 25, durationSeconds: 50.0);
        // B: 25 clears, 50s, on 19x19 (area 361 > 324) — strictly better via final tiebreak.
        await InsertEndlessScoreAsync(uB, 19, 19, clears: 25, durationSeconds: 50.0);
        // C: 25 clears, 30s — better than both via duration tiebreak.
        await InsertEndlessScoreAsync(uC, 18, 18, clears: 25, durationSeconds: 30.0);

        var resp = await _client.GetAsync("/api/endless-leaderboards/all");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var lb = await resp.Content.ReadFromJsonAsync<EndlessLbResponse>();

        // Find our three users in the global response (other suites in
        // the fixture may have planted rows too).
        var c = lb!.Entries.Find(e => e.DisplayName == "AllC");
        var b = lb.Entries.Find(e => e.DisplayName == "AllB");
        var a = lb.Entries.Find(e => e.DisplayName == "AllA");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);

        // C ranks above B (lower duration); B ranks above A (same duration
        // + clears, larger board area).
        Assert.True(c!.Rank < b!.Rank, $"C(rank={c.Rank}) should rank above B(rank={b.Rank})");
        Assert.True(b.Rank < a!.Rank, $"B(rank={b.Rank}) should rank above A(rank={a.Rank})");

        // Board dimensions are surfaced in /all entries.
        Assert.Equal(18, c.BoardWidth);
        Assert.Equal(19, b.BoardWidth);
    }

    [Fact]
    public async Task GetAll_ExcludesFlaggedUsers()
    {
        var (uClean, _) = await RegisterAsync("end-lb-all-clean@test.com", "GlobalClean");
        var (uDirty, _) = await RegisterAsync("end-lb-all-dirty@test.com", "GlobalDirty");
        await InsertEndlessScoreAsync(uClean, 20, 20, clears: 99, durationSeconds: 5.0);
        await InsertEndlessScoreAsync(uDirty, 20, 20, clears: 100, durationSeconds: 5.0);
        await FlagUserAsync(uDirty);

        var resp = await _client.GetAsync("/api/endless-leaderboards/all");
        var lb = await resp.Content.ReadFromJsonAsync<EndlessLbResponse>();

        Assert.Null(lb!.Entries.Find(e => e.DisplayName == "GlobalDirty"));
        Assert.NotNull(lb.Entries.Find(e => e.DisplayName == "GlobalClean"));
    }
}

internal record EndlessLbAuthResponse(string Token, string DisplayName);

internal class EndlessLbResponse
{
    public int TotalEntries { get; set; }
    public List<EndlessLbEntry> Entries { get; set; } = new();
}

internal class EndlessLbEntry
{
    public int Rank { get; set; }
    public string DisplayName { get; set; } = "";
    public int Clears { get; set; }
    public int LongestCombo { get; set; }
    public double DurationSeconds { get; set; }
    public string GameId { get; set; } = "";
    public int? BoardWidth { get; set; }
    public int? BoardHeight { get; set; }
}

internal class EndlessPlayerEntry
{
    public int Rank { get; set; }
    public int TotalEntries { get; set; }
    public int Clears { get; set; }
    public int LongestCombo { get; set; }
    public double DurationSeconds { get; set; }
    public string GameId { get; set; } = "";
    public int? BoardWidth { get; set; }
    public int? BoardHeight { get; set; }
    public bool Flagged { get; set; }
}
