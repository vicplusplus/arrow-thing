using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArrowThing.Server;
using ArrowThing.Server.Data;
using ArrowThing.Server.Games;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Phase 4: gzipped replay storage round-trip + the combined-COUNT
/// leaderboard query. Both are operational changes that must keep the
/// existing API contract intact.
/// </summary>
public class StoragePerfTests : IClassFixture<TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;
    private readonly string _deviceId = Guid.NewGuid().ToString("N");

    public StoragePerfTests(TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Device-Id", _deviceId);
    }

    public void Dispose() => _client.Dispose();

    // -- GzipUtil round trip --

    [Fact]
    public void GzipUtil_RoundTrip_PreservesUtf8()
    {
        const string input = "héllo, 🎮 — events: [{\"seq\":0,\"type\":\"clear\"}]";
        var compressed = GzipUtil.Compress(input);
        Assert.NotEmpty(compressed);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(input), compressed);

        var roundTripped = GzipUtil.Decompress(compressed);
        Assert.Equal(input, roundTripped);
    }

    [Fact]
    public void GzipUtil_EmptyInput_RoundTrips()
    {
        Assert.Empty(GzipUtil.Compress(""));
        Assert.Equal("", GzipUtil.Decompress(Array.Empty<byte>()));
    }

    // -- ReplayStorage prefers gz, falls back to text --

    [Fact]
    public void ReplayStorage_PrefersGzOverText()
    {
        var score = new Score
        {
            ReplayJson = "legacy-text",
            ReplayJsonGz = GzipUtil.Compress("modern-gz"),
        };
        Assert.Equal("modern-gz", ReplayStorage.Get(score));
    }

    [Fact]
    public void ReplayStorage_FallsBackToText_WhenGzNullOrEmpty()
    {
        var nullGz = new Score { ReplayJson = "legacy-text", ReplayJsonGz = null };
        Assert.Equal("legacy-text", ReplayStorage.Get(nullGz));

        var emptyGz = new Score { ReplayJson = "legacy-text", ReplayJsonGz = Array.Empty<byte>() };
        Assert.Equal("legacy-text", ReplayStorage.Get(emptyGz));
    }

    [Fact]
    public void ReplayStorage_Set_PopulatesGz_AndClearsText()
    {
        var score = new Score { ReplayJson = "old", ReplayJsonGz = null };
        ReplayStorage.Set(score, "new payload");

        Assert.NotNull(score.ReplayJsonGz);
        Assert.NotEmpty(score.ReplayJsonGz!);
        Assert.Equal("", score.ReplayJson);
        Assert.Equal("new payload", ReplayStorage.Get(score));
    }

    // -- Score persists round-trip via the EF DbContext --

    [Fact]
    public async Task Score_PersistedReplay_ReadsBackIdentically()
    {
        // Use an isolated user so we don't conflict with other tests'
        // (UserId, BoardWidth, BoardHeight) unique-index rows.
        var userId = Guid.NewGuid();
        var gameId = Guid.NewGuid();
        const string replayPayload = "{\"events\":[{\"seq\":0,\"type\":\"start\"}]}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(
                new User
                {
                    Id = userId,
                    Email = $"perf-{userId:N}@example.com",
                    DisplayName = "Perf",
                    PasswordHash = "x",
                    CreatedAt = DateTime.UtcNow,
                    EmailVerifiedAt = DateTime.UtcNow,
                }
            );
            var score = new Score
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                GameId = gameId,
                Seed = 7,
                BoardWidth = 11,
                BoardHeight = 13,
                MaxArrowLength = 5,
                Time = 4.2,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ReplayStorage.Set(score, replayPayload);
            db.Scores.Add(score);
            await db.SaveChangesAsync();
        }

        // Re-read in a fresh scope so EF can't return the in-memory entity.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loaded = await db.Scores.AsNoTracking().FirstAsync(s => s.GameId == gameId);
            Assert.NotNull(loaded.ReplayJsonGz);
            Assert.NotEmpty(loaded.ReplayJsonGz!);
            // The legacy text column should be empty for new writes.
            Assert.Equal("", loaded.ReplayJson);
            Assert.Equal(replayPayload, ReplayStorage.Get(loaded));
        }
    }

    // -- Combined leaderboard COUNT: rank+total match the legacy two-query result --

    [Fact]
    public async Task GetPlayerEntry_ReturnsCorrectRankAndTotal()
    {
        // Three users, three scores at the same board size. Their times
        // place them at ranks 1, 2, 3 with total=3.
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        const int width = 21,
            height = 21;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedUserAsync(db, a, "rankA", flagged: false);
            await SeedUserAsync(db, b, "rankB", flagged: false);
            await SeedUserAsync(db, c, "rankC", flagged: false);
            await SeedScoreAsync(db, a, width, height, time: 1.0);
            await SeedScoreAsync(db, b, width, height, time: 2.0);
            await SeedScoreAsync(db, c, width, height, time: 3.0);
        }

        using var scope2 = _factory.Services.CreateScope();
        var leaderboards =
            scope2.ServiceProvider.GetRequiredService<ArrowThing.Server.Leaderboards.LeaderboardService>();

        var entryA = await leaderboards.GetPlayerEntryAsync(a, width, height);
        Assert.NotNull(entryA);
        Assert.Equal(1, entryA!.Rank);
        Assert.Equal(3, entryA.TotalEntries);

        var entryB = await leaderboards.GetPlayerEntryAsync(b, width, height);
        Assert.NotNull(entryB);
        Assert.Equal(2, entryB!.Rank);
        Assert.Equal(3, entryB.TotalEntries);

        var entryC = await leaderboards.GetPlayerEntryAsync(c, width, height);
        Assert.NotNull(entryC);
        Assert.Equal(3, entryC!.Rank);
        Assert.Equal(3, entryC.TotalEntries);
    }

    [Fact]
    public async Task GetPlayerEntry_ExcludesFlaggedFromTotal()
    {
        var (legit, flagged) = (Guid.NewGuid(), Guid.NewGuid());
        const int width = 23,
            height = 23;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedUserAsync(db, legit, "legit", flagged: false);
            await SeedUserAsync(db, flagged, "flagged", flagged: true);
            await SeedScoreAsync(db, legit, width, height, time: 5.0);
            await SeedScoreAsync(db, flagged, width, height, time: 1.0);
        }

        using var scope2 = _factory.Services.CreateScope();
        var leaderboards =
            scope2.ServiceProvider.GetRequiredService<ArrowThing.Server.Leaderboards.LeaderboardService>();

        var entry = await leaderboards.GetPlayerEntryAsync(legit, width, height);
        Assert.NotNull(entry);
        // Total counts only the non-flagged user; legit is rank 1 of 1 even
        // though the flagged user has a faster time.
        Assert.Equal(1, entry!.Rank);
        Assert.Equal(1, entry.TotalEntries);
    }

    // -- Helpers --

    private static Task SeedUserAsync(AppDbContext db, Guid id, string name, bool flagged)
    {
        db.Users.Add(
            new User
            {
                Id = id,
                Email = $"{name}-{id:N}@example.com",
                DisplayName = name,
                PasswordHash = "x",
                CreatedAt = DateTime.UtcNow,
                EmailVerifiedAt = DateTime.UtcNow,
                Flagged = flagged,
            }
        );
        return db.SaveChangesAsync();
    }

    private static Task SeedScoreAsync(
        AppDbContext db,
        Guid userId,
        int width,
        int height,
        double time
    )
    {
        var score = new Score
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GameId = Guid.NewGuid(),
            Seed = 1,
            BoardWidth = width,
            BoardHeight = height,
            MaxArrowLength = 5,
            Time = time,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ReplayStorage.Set(score, "{\"events\":[]}");
        db.Scores.Add(score);
        return db.SaveChangesAsync();
    }
}
