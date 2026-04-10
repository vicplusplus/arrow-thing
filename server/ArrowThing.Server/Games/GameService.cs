using ArrowThing.Server.Data;
using ArrowThing.Server.Leaderboards;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArrowThing.Server.Games;

public class GameService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GameService> _logger;
    private readonly LeaderboardCache _cache;

    private const int RateLimitPerHour = 10;
    private const int TopSnapshotCount = 50;

    public GameService(AppDbContext db, ILogger<GameService> logger, LeaderboardCache cache)
    {
        _db = db;
        _logger = logger;
        _cache = cache;
    }

    public async Task<(SubmitResultResponse? data, int status, string? error)> SubmitReplayAsync(
        Guid userId,
        string replayJson
    )
    {
        // Deserialize
        ReplayData replay;
        try
        {
            replay = JsonConvert.DeserializeObject<ReplayData>(replayJson)!;
        }
        catch
        {
            return (null, 400, "Malformed replay JSON.");
        }

        if (replay == null)
            return (null, 400, "Malformed replay JSON.");

        if (!Guid.TryParse(replay.gameId, out var gameId))
            return (null, 400, "Invalid gameId.");

        // Reject submissions from flagged accounts
        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return (null, 401, "User not found.");
        if (user.Flagged)
            return (null, 403, "Account is flagged. Contact support.");

        // Pre-verification: cheap static checks before expensive board regeneration
        var preVerifyReason = ReplayVerifier.PreVerify(replay);
        if (preVerifyReason != null)
        {
            user.Flagged = true;
            user.FlagReason = preVerifyReason;
            await _db.SaveChangesAsync();
            return (null, 403, "Account is flagged. Contact support.");
        }

        // Idempotency check
        var existing = await _db.Scores.FirstOrDefaultAsync(s =>
            s.UserId == userId
            && s.BoardWidth == replay.boardWidth
            && s.BoardHeight == replay.boardHeight
        );

        if (existing != null && existing.GameId == gameId)
        {
            var existingRank = await ComputeRank(
                existing.BoardWidth,
                existing.BoardHeight,
                existing.Time
            );
            return (
                new SubmitResultResponse
                {
                    Verified = true,
                    Rank = existingRank,
                    IsPersonalBest = false,
                },
                200,
                null
            );
        }

        // Seed duplicate detection: flag if another user has the same seed + board size
        var duplicate = await _db.Scores.FirstOrDefaultAsync(s =>
            s.Seed == replay.seed
            && s.BoardWidth == replay.boardWidth
            && s.BoardHeight == replay.boardHeight
            && s.UserId != userId
        );
        bool flagged = duplicate != null;
        if (flagged)
        {
            user.Flagged = true;
            user.FlagReason = "duplicate_seed";
        }

        // Rate limit: count verified score updates in the past hour
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentUpdates = await _db.Scores.CountAsync(s =>
            s.UserId == userId && s.UpdatedAt >= oneHourAgo
        );
        if (recentUpdates >= RateLimitPerHour)
            return (null, 429, "Rate limit exceeded. Try again later.");

        // Verify
        var result = ReplayVerifier.Verify(replay);
        if (!result.IsValid)
            return (
                new SubmitResultResponse { Verified = false, Reason = result.Reason },
                200,
                null
            );

        // Check if this is an improvement
        if (existing != null && existing.Time <= result.VerifiedTime)
        {
            var rank = await ComputeRank(existing.BoardWidth, existing.BoardHeight, existing.Time);
            return (
                new SubmitResultResponse
                {
                    Verified = true,
                    Rank = rank,
                    IsPersonalBest = false,
                },
                200,
                null
            );
        }

        // Compute rank for the new score (exclude flagged from ranking)
        var newRank = await ComputeRank(replay.boardWidth, replay.boardHeight, result.VerifiedTime);

        // Prepare stored replay JSON (snapshot handling)
        var storedReplayJson =
            newRank <= TopSnapshotCount ? CompressSnapshot(replayJson) : StripSnapshot(replayJson);

        var now = DateTime.UtcNow;

        if (existing != null)
        {
            existing.GameId = gameId;
            existing.Seed = replay.seed;
            existing.MaxArrowLength = replay.maxArrowLength;
            existing.Time = result.VerifiedTime;
            existing.ReplayJson = storedReplayJson;
            existing.UpdatedAt = now;
            existing.Flagged = flagged;
            existing.FlagReason = flagged ? "duplicate_seed" : null;
        }
        else
        {
            var score = new Score
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                GameId = gameId,
                Seed = replay.seed,
                BoardWidth = replay.boardWidth,
                BoardHeight = replay.boardHeight,
                MaxArrowLength = replay.maxArrowLength,
                Time = result.VerifiedTime,
                ReplayJson = storedReplayJson,
                CreatedAt = now,
                UpdatedAt = now,
                Flagged = flagged,
                FlagReason = flagged ? "duplicate_seed" : null,
            };
            _db.Scores.Add(score);
        }

        await _db.SaveChangesAsync();

        // Invalidate cached leaderboard for this board size
        _cache.Invalidate(replay.boardWidth, replay.boardHeight);

        // If this entered top-50, strip snapshot from displaced score
        if (newRank <= TopSnapshotCount)
            await StripDisplacedSnapshot(replay.boardWidth, replay.boardHeight);

        return (
            new SubmitResultResponse
            {
                Verified = true,
                Rank = newRank,
                IsPersonalBest = true,
            },
            200,
            null
        );
    }

    private async Task<int> ComputeRank(int width, int height, double time)
    {
        var betterCount = await _db.Scores.CountAsync(s =>
            s.BoardWidth == width && s.BoardHeight == height && !s.Flagged && s.Time < time
        );
        return betterCount + 1;
    }

    private async Task StripDisplacedSnapshot(int width, int height)
    {
        // Find the score at position 51 (if it exists) and strip its snapshot.
        var displaced = await _db
            .Scores.Where(s => s.BoardWidth == width && s.BoardHeight == height)
            .OrderBy(s => s.Time)
            .Skip(TopSnapshotCount)
            .Take(1)
            .FirstOrDefaultAsync();

        if (displaced != null && HasSnapshot(displaced.ReplayJson))
        {
            displaced.ReplayJson = StripSnapshot(displaced.ReplayJson);
            await _db.SaveChangesAsync();
        }
    }

    private static bool HasSnapshot(string replayJson)
    {
        try
        {
            var obj = JObject.Parse(replayJson);
            return obj["boardSnapshot"] != null;
        }
        catch
        {
            return false;
        }
    }

    private static string StripSnapshot(string replayJson)
    {
        try
        {
            var obj = JObject.Parse(replayJson);
            obj.Remove("boardSnapshot");
            return obj.ToString(Formatting.None);
        }
        catch
        {
            return replayJson;
        }
    }

    private static string CompressSnapshot(string replayJson)
    {
        try
        {
            var obj = JObject.Parse(replayJson);
            var snapshot = obj["boardSnapshot"];
            if (snapshot == null || snapshot.Type == JTokenType.Null)
                return replayJson;

            // If already a string (pre-compressed), leave as-is.
            if (snapshot.Type == JTokenType.String)
                return replayJson;

            // Gzip-compress and base64-encode the snapshot array.
            var snapshotJson = snapshot.ToString(Formatting.None);
            var bytes = System.Text.Encoding.UTF8.GetBytes(snapshotJson);
            using var ms = new System.IO.MemoryStream();
            using (
                var gz = new System.IO.Compression.GZipStream(
                    ms,
                    System.IO.Compression.CompressionLevel.Optimal
                )
            )
            {
                gz.Write(bytes, 0, bytes.Length);
            }
            obj["boardSnapshot"] = Convert.ToBase64String(ms.ToArray());
            return obj.ToString(Formatting.None);
        }
        catch
        {
            return replayJson;
        }
    }
}

public class SubmitReplayRequest
{
    public string ReplayJson { get; set; } = "";
}

public class SubmitResultResponse
{
    public bool Verified { get; set; }
    public int? Rank { get; set; }
    public bool? IsPersonalBest { get; set; }
    public string? Reason { get; set; }
}
