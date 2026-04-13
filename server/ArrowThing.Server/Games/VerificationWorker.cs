using System.Text.Json;
using ArrowThing.Server.Data;
using ArrowThing.Server.Leaderboards;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;

namespace ArrowThing.Server.Games;

public class VerificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly LeaderboardCache _leaderboardCache;
    private readonly ILogger<VerificationWorker> _logger;

    private const string ResultKeyPrefix = "verify:result:";
    private static readonly TimeSpan ResultTtl = TimeSpan.FromHours(1);
    private const int TopSnapshotCount = 50;

    public VerificationWorker(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        LeaderboardCache leaderboardCache,
        ILogger<VerificationWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _leaderboardCache = leaderboardCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Verification worker started");
        var db = _redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Poll standard queue first (small boards), then heavy queue.
                var result = await db.ListRightPopAsync(GameService.StandardQueueKey);
                if (result.IsNullOrEmpty)
                    result = await db.ListRightPopAsync(GameService.HeavyQueueKey);

                if (result.IsNullOrEmpty)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                await ProcessJob(db, (string)result!);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing verification job");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("Verification worker stopped");
    }

    private async Task ProcessJob(IDatabase redis, string jobJson)
    {
        VerificationJob? job;
        try
        {
            job = System.Text.Json.JsonSerializer.Deserialize<VerificationJob>(jobJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize verification job: {Json}", jobJson);
            return;
        }

        if (job == null || string.IsNullOrEmpty(job.GameId))
        {
            _logger.LogWarning("Deserialized verification job is null or missing GameId");
            return;
        }

        _logger.LogInformation(
            "Processing verification for game {GameId}, user {UserId}",
            job.GameId,
            job.UserId
        );

        ReplayData replay;
        try
        {
            replay = JsonConvert.DeserializeObject<ReplayData>(job.ReplayJson)!;
        }
        catch
        {
            await WriteResult(
                redis,
                job.GameId,
                new VerificationResultPayload { Status = "rejected", Reason = "Malformed replay." }
            );
            return;
        }

        // Run full verification
        var verifyResult = ReplayVerifier.Verify(replay);

        if (!verifyResult.IsValid)
        {
            _logger.LogInformation(
                "Verification failed for game {GameId}: {Reason}",
                job.GameId,
                verifyResult.Reason
            );

            // Flag user account on verification failure
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FindAsync(Guid.Parse(job.UserId));
            if (user != null)
            {
                user.Flagged = true;
                user.FlagReason = $"verification_failed: {verifyResult.Reason}";
                await db.SaveChangesAsync();
            }

            await WriteResult(
                redis,
                job.GameId,
                new VerificationResultPayload { Status = "rejected", Reason = verifyResult.Reason }
            );
            return;
        }

        // Persist score
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = Guid.Parse(job.UserId);
            var gameId = Guid.Parse(job.GameId);

            var existing = await db.Scores.FirstOrDefaultAsync(s =>
                s.UserId == userId
                && s.BoardWidth == replay.boardWidth
                && s.BoardHeight == replay.boardHeight
            );

            // Check if this is an improvement
            if (existing != null && existing.Time <= verifyResult.VerifiedTime)
            {
                var rank = await ComputeRank(
                    db,
                    existing.BoardWidth,
                    existing.BoardHeight,
                    existing.Time
                );

                await WriteResult(
                    redis,
                    job.GameId,
                    new VerificationResultPayload
                    {
                        Status = "verified",
                        Rank = rank,
                        IsPersonalBest = false,
                    }
                );
                return;
            }

            var newRank = await ComputeRank(
                db,
                replay.boardWidth,
                replay.boardHeight,
                verifyResult.VerifiedTime
            );

            var storedReplayJson =
                newRank <= TopSnapshotCount
                    ? CompressSnapshot(job.ReplayJson)
                    : StripSnapshot(job.ReplayJson);

            var now = DateTime.UtcNow;

            if (existing != null)
            {
                existing.GameId = gameId;
                existing.Seed = replay.seed;
                existing.MaxArrowLength = replay.maxArrowLength;
                existing.Time = verifyResult.VerifiedTime;
                existing.ReplayJson = storedReplayJson;
                existing.UpdatedAt = now;
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
                    Time = verifyResult.VerifiedTime,
                    ReplayJson = storedReplayJson,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Scores.Add(score);
            }

            await db.SaveChangesAsync();

            _leaderboardCache.Invalidate(replay.boardWidth, replay.boardHeight);

            if (newRank <= TopSnapshotCount)
                await StripDisplacedSnapshot(db, replay.boardWidth, replay.boardHeight);

            _logger.LogInformation(
                "Verified game {GameId}: rank {Rank}, personalBest=true",
                job.GameId,
                newRank
            );

            await WriteResult(
                redis,
                job.GameId,
                new VerificationResultPayload
                {
                    Status = "verified",
                    Rank = newRank,
                    IsPersonalBest = true,
                }
            );
        }
    }

    private static async Task WriteResult(
        IDatabase redis,
        string gameId,
        VerificationResultPayload result
    )
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            result,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }
        );
        await redis.StringSetAsync($"{ResultKeyPrefix}{gameId}", json, ResultTtl);
    }

    private static async Task<int> ComputeRank(AppDbContext db, int width, int height, double time)
    {
        var betterCount = await db.Scores.CountAsync(s =>
            s.BoardWidth == width && s.BoardHeight == height && !s.User.Flagged && s.Time < time
        );
        return betterCount + 1;
    }

    private static async Task StripDisplacedSnapshot(AppDbContext db, int width, int height)
    {
        var displaced = await db
            .Scores.Where(s => s.BoardWidth == width && s.BoardHeight == height)
            .OrderBy(s => s.Time)
            .Skip(TopSnapshotCount)
            .Take(1)
            .FirstOrDefaultAsync();

        if (displaced != null && HasSnapshot(displaced.ReplayJson))
        {
            displaced.ReplayJson = StripSnapshot(displaced.ReplayJson);
            await db.SaveChangesAsync();
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

            if (snapshot.Type == JTokenType.String)
                return replayJson;

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

public class VerificationJob
{
    public string UserId { get; set; } = "";
    public string GameId { get; set; } = "";
    public string ReplayJson { get; set; } = "";
    public string EnqueuedAt { get; set; } = "";
}

public class VerificationResultPayload
{
    public string Status { get; set; } = "";
    public int? Rank { get; set; }
    public bool? IsPersonalBest { get; set; }
    public string? Reason { get; set; }
}
