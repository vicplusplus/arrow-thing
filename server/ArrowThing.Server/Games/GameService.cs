using System.Text.Json;
using ArrowThing.Server.Data;
using ArrowThing.Server.Leaderboards;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace ArrowThing.Server.Games;

public class GameService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GameService> _logger;
    private readonly LeaderboardCache _cache;
    private readonly IConnectionMultiplexer? _redis;

    private const int RateLimitPerHour = 10;
    private const int HeavyBoardAreaThreshold = 2500; // ~50x50
    private const int MaxStandardQueueDepth = 100;
    private const int MaxHeavyQueueDepth = 20;
    internal const string StandardQueueKey = "verify:queue:standard";
    internal const string HeavyQueueKey = "verify:queue:heavy";

    public GameService(
        AppDbContext db,
        ILogger<GameService> logger,
        LeaderboardCache cache,
        IConnectionMultiplexer? redis = null
    )
    {
        _db = db;
        _logger = logger;
        _cache = cache;
        _redis = redis;
    }

    public async Task<(object? data, int status, string? error)> SubmitReplayAsync(
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

        // Replay version gate — runs BEFORE any flagging path. Old-format replays
        // cannot be regenerated deterministically by the current verifier (e.g.
        // after an RNG change), so we reject them up-front as "please update"
        // rather than letting the verifier flag the user as a cheater.
        if (!ReplayVersionPolicy.IsAllowed(replay.version))
        {
            _logger.LogInformation(
                "Rejecting submission with outdated replay version {Version} for user {UserId}",
                replay.version,
                userId
            );
            return (null, 426, ReplayVersionPolicy.RejectionMessage(replay.version));
        }

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
            user.FlaggedAt = DateTime.UtcNow;
            user.FlagTriggerSeed = replay.seed;
            user.FlagTriggerBoardWidth = replay.boardWidth;
            user.FlagTriggerBoardHeight = replay.boardHeight;
            user.FlagTriggerGameId = replay.gameId;
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

        // Seed duplicate detection: flag user and reject
        var duplicate = await _db.Scores.AnyAsync(s =>
            s.Seed == replay.seed
            && s.BoardWidth == replay.boardWidth
            && s.BoardHeight == replay.boardHeight
            && s.UserId != userId
        );
        if (duplicate)
        {
            user.Flagged = true;
            user.FlagReason = "duplicate_seed";
            user.FlaggedAt = DateTime.UtcNow;
            user.FlagTriggerSeed = replay.seed;
            user.FlagTriggerBoardWidth = replay.boardWidth;
            user.FlagTriggerBoardHeight = replay.boardHeight;
            user.FlagTriggerGameId = replay.gameId;
            await _db.SaveChangesAsync();
            return (null, 403, "Account is flagged. Contact support.");
        }

        // Rate limit: count verified score updates in the past hour
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentUpdates = await _db.Scores.CountAsync(s =>
            s.UserId == userId && s.UpdatedAt >= oneHourAgo
        );
        if (recentUpdates >= RateLimitPerHour)
            return (null, 429, "Rate limit exceeded. Try again later.");

        // Enqueue for async verification
        if (_redis == null)
            return (null, 503, "Verification service unavailable.");

        var area = replay.boardWidth * replay.boardHeight;
        var isHeavy = area > HeavyBoardAreaThreshold;
        var queueKey = isHeavy ? HeavyQueueKey : StandardQueueKey;
        var maxDepth = isHeavy ? MaxHeavyQueueDepth : MaxStandardQueueDepth;

        var redisDb = _redis.GetDatabase();
        var queueLength = await redisDb.ListLengthAsync(queueKey);
        if (queueLength >= maxDepth)
            return (null, 503, "Server busy, try again later.");

        var job = new
        {
            UserId = userId.ToString(),
            GameId = gameId.ToString(),
            ReplayJson = replayJson,
            EnqueuedAt = DateTime.UtcNow.ToString("O"),
        };

        await redisDb.ListLeftPushAsync(queueKey, System.Text.Json.JsonSerializer.Serialize(job));

        return (
            new SubmitAcceptedResponse { GameId = gameId.ToString(), Status = "pending" },
            202,
            null
        );
    }

    private async Task<int> ComputeRank(int width, int height, double time)
    {
        var betterCount = await _db.Scores.CountAsync(s =>
            s.BoardWidth == width && s.BoardHeight == height && !s.User.Flagged && s.Time < time
        );
        return betterCount + 1;
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

public class SubmitAcceptedResponse
{
    public string GameId { get; set; } = "";
    public string Status { get; set; } = "";
}
