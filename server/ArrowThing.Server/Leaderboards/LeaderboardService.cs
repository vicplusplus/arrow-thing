using ArrowThing.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Leaderboards;

public class LeaderboardService
{
    private readonly AppDbContext _db;
    private readonly LeaderboardCache _cache;

    public LeaderboardService(AppDbContext db, LeaderboardCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<LeaderboardResponse> GetTopEntriesAsync(int width, int height)
    {
        var cached = _cache.Get(width, height);
        if (cached != null)
            return cached;

        var totalEntries = await _db.Scores.CountAsync(s =>
            s.BoardWidth == width && s.BoardHeight == height && !s.User.Flagged
        );

        var entries = await _db
            .Scores.Where(s => s.BoardWidth == width && s.BoardHeight == height && !s.User.Flagged)
            .OrderBy(s => s.Time)
            .Take(50)
            .Select(s => new LeaderboardEntryDto
            {
                DisplayName = s.User.DisplayName,
                Time = s.Time,
                GameId = s.GameId.ToString(),
            })
            .ToListAsync();

        // Assign ranks (1-based)
        for (int i = 0; i < entries.Count; i++)
            entries[i].Rank = i + 1;

        var response = new LeaderboardResponse { TotalEntries = totalEntries, Entries = entries };
        _cache.Set(width, height, response);
        return response;
    }

    public async Task<LeaderboardResponse> GetTopEntriesAllAsync()
    {
        var cached = _cache.Get(0, 0);
        if (cached != null)
            return cached;

        // For each user, pick their representative score (largest board area, then
        // fastest time) and return the global top 50. Uses PostgreSQL DISTINCT ON
        // to avoid loading all scores into memory.
        var rows = await _db
            .Database.SqlQueryRaw<AllLeaderboardRow>(
                """
                SELECT sub."DisplayName", sub."Time", sub."GameId",
                       sub."BoardWidth", sub."BoardHeight"
                FROM (
                    SELECT DISTINCT ON (s."UserId")
                        u."DisplayName", s."Time", s."GameId"::text AS "GameId",
                        s."BoardWidth", s."BoardHeight"
                    FROM "Scores" s
                    INNER JOIN "Users" u ON s."UserId" = u."Id"
                    WHERE NOT u."Flagged"
                    ORDER BY s."UserId",
                             (s."BoardWidth" * s."BoardHeight") DESC,
                             s."Time" ASC
                ) sub
                ORDER BY (sub."BoardWidth" * sub."BoardHeight") DESC,
                         sub."Time" ASC
                LIMIT 50
                """
            )
            .ToListAsync();

        var totalCount = await _db
            .Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(DISTINCT s."UserId")::int AS "Value"
                FROM "Scores" s
                INNER JOIN "Users" u ON s."UserId" = u."Id"
                WHERE NOT u."Flagged"
                """
            )
            .SingleAsync();

        var entries = rows
            .Select(
                (r, i) =>
                    new LeaderboardEntryDto
                    {
                        Rank = i + 1,
                        DisplayName = r.DisplayName,
                        Time = r.Time,
                        GameId = r.GameId,
                        BoardWidth = r.BoardWidth,
                        BoardHeight = r.BoardHeight,
                    }
            )
            .ToList();

        var response = new LeaderboardResponse { TotalEntries = totalCount, Entries = entries };
        _cache.Set(0, 0, response);
        return response;
    }

    public async Task<PlayerEntryDto?> GetPlayerEntryAsync(Guid userId, int width, int height)
    {
        var score = await _db
            .Scores.Include(s => s.User)
            .FirstOrDefaultAsync(s =>
                s.UserId == userId && s.BoardWidth == width && s.BoardHeight == height
            );

        if (score == null)
            return null;

        var rank =
            await _db.Scores.CountAsync(s =>
                s.BoardWidth == width
                && s.BoardHeight == height
                && !s.User.Flagged
                && s.Time < score.Time
            ) + 1;

        var totalEntries = await _db.Scores.CountAsync(s =>
            s.BoardWidth == width && s.BoardHeight == height && !s.User.Flagged
        );

        return new PlayerEntryDto
        {
            Rank = rank,
            TotalEntries = totalEntries,
            Time = score.Time,
            GameId = score.GameId.ToString(),
            Flagged = score.User.Flagged,
        };
    }

    public async Task<PlayerEntryDto?> GetPlayerEntryAllAsync(Guid userId)
    {
        // Find user's representative score (largest board area, then fastest time).
        // Only loads this user's scores — small set.
        var representative = await _db
            .Scores.Include(s => s.User)
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.BoardWidth * s.BoardHeight)
            .ThenBy(s => s.Time)
            .FirstOrDefaultAsync();

        if (representative == null)
            return null;

        var repArea = representative.BoardWidth * representative.BoardHeight;

        // Compute rank and total via SQL using DISTINCT ON — avoids loading all scores.
        var rankResult = await _db
            .Database.SqlQueryRaw<int>(
                """
                WITH representatives AS (
                    SELECT DISTINCT ON (s."UserId")
                        (s."BoardWidth" * s."BoardHeight") AS area,
                        s."Time"
                    FROM "Scores" s
                    INNER JOIN "Users" u ON s."UserId" = u."Id"
                    WHERE NOT u."Flagged"
                    ORDER BY s."UserId",
                             (s."BoardWidth" * s."BoardHeight") DESC,
                             s."Time" ASC
                )
                SELECT COUNT(*)::int AS "Value"
                FROM representatives
                WHERE area > {0} OR (area = {0} AND "Time" < {1})
                """,
                repArea,
                representative.Time
            )
            .SingleAsync();

        var totalEntries = await _db
            .Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(DISTINCT s."UserId")::int AS "Value"
                FROM "Scores" s
                INNER JOIN "Users" u ON s."UserId" = u."Id"
                WHERE NOT u."Flagged"
                """
            )
            .SingleAsync();

        return new PlayerEntryDto
        {
            Rank = rankResult + 1,
            TotalEntries = totalEntries,
            Time = representative.Time,
            GameId = representative.GameId.ToString(),
            BoardWidth = representative.BoardWidth,
            BoardHeight = representative.BoardHeight,
            Flagged = representative.User.Flagged,
        };
    }

    public async Task<string?> GetReplayAsync(string gameIdStr)
    {
        if (!Guid.TryParse(gameIdStr, out var gameId))
            return null;

        var score = await _db.Scores.FirstOrDefaultAsync(s => s.GameId == gameId);
        return score?.ReplayJson;
    }
}

public class LeaderboardResponse
{
    public int TotalEntries { get; set; }
    public List<LeaderboardEntryDto> Entries { get; set; } = new();
}

public class LeaderboardEntryDto
{
    public int Rank { get; set; }
    public string DisplayName { get; set; } = "";
    public double Time { get; set; }
    public string GameId { get; set; } = "";
    public int? BoardWidth { get; set; }
    public int? BoardHeight { get; set; }
}

public class PlayerEntryDto
{
    public int Rank { get; set; }
    public int TotalEntries { get; set; }
    public double Time { get; set; }
    public string GameId { get; set; } = "";
    public int? BoardWidth { get; set; }
    public int? BoardHeight { get; set; }
    public bool Flagged { get; set; }
}

/// <summary>Row type for the raw SQL global leaderboard query.</summary>
public class AllLeaderboardRow
{
    public string DisplayName { get; set; } = "";
    public double Time { get; set; }
    public string GameId { get; set; } = "";
    public int BoardWidth { get; set; }
    public int BoardHeight { get; set; }
}
