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

    public async Task<LeaderboardResponse> GetTopEntriesAsync(int width, int height, int limit = 50)
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
            .Take(limit)
            .Select(s => new LeaderboardEntryDto
            {
                DisplayName = s.User.DisplayName,
                Time = s.Time,
                GameId = s.GameId.ToString(),
                Id = s.Id,
            })
            .ToListAsync();

        // Assign ranks (1-based)
        for (int i = 0; i < entries.Count; i++)
            entries[i].Rank = i + 1;

        var response = new LeaderboardResponse { TotalEntries = totalEntries, Entries = entries };
        _cache.Set(width, height, response);
        return response;
    }

    public async Task<LeaderboardResponse> GetTopEntriesAllAsync(int limit = 50)
    {
        var cached = _cache.Get(0, 0);
        if (cached != null)
            return cached;

        // For each user, find their score with the largest board area, then fastest time.
        // This uses a raw approach: get all scores grouped by user, pick representative.
        var allScores = await _db
            .Scores.Where(s => !s.User.Flagged)
            .Include(s => s.User)
            .ToListAsync();

        var byUser = allScores
            .GroupBy(s => s.UserId)
            .Select(g =>
            {
                // Representative: max area, then min time
                return g.OrderByDescending(s => s.BoardWidth * s.BoardHeight)
                    .ThenBy(s => s.Time)
                    .First();
            })
            .OrderByDescending(s => s.BoardWidth * s.BoardHeight)
            .ThenBy(s => s.Time)
            .ToList();

        var totalEntries = byUser.Count;

        var entries = byUser
            .Take(limit)
            .Select(
                (s, i) =>
                    new LeaderboardEntryDto
                    {
                        Rank = i + 1,
                        DisplayName = s.User.DisplayName,
                        Time = s.Time,
                        GameId = s.GameId.ToString(),
                        BoardWidth = s.BoardWidth,
                        BoardHeight = s.BoardHeight,
                        Id = s.Id,
                    }
            )
            .ToList();

        var response = new LeaderboardResponse { TotalEntries = totalEntries, Entries = entries };
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
            FlagReason = score.User.FlagReason,
        };
    }

    public async Task<PlayerEntryDto?> GetPlayerEntryAllAsync(Guid userId)
    {
        var userScores = await _db
            .Scores.Include(s => s.User)
            .Where(s => s.UserId == userId)
            .ToListAsync();

        if (userScores.Count == 0)
            return null;

        // User's representative score: largest area, then fastest time
        var representative = userScores
            .OrderByDescending(s => s.BoardWidth * s.BoardHeight)
            .ThenBy(s => s.Time)
            .First();

        // Compute rank among all users' representative scores
        var allScores = await _db.Scores.Where(s => !s.User.Flagged).ToListAsync();
        var allRepresentatives = allScores
            .GroupBy(s => s.UserId)
            .Select(g =>
                g.OrderByDescending(s => s.BoardWidth * s.BoardHeight).ThenBy(s => s.Time).First()
            )
            .ToList();

        var repArea = representative.BoardWidth * representative.BoardHeight;
        var rank =
            allRepresentatives.Count(r =>
            {
                var rArea = r.BoardWidth * r.BoardHeight;
                return rArea > repArea || (rArea == repArea && r.Time < representative.Time);
            }) + 1;

        return new PlayerEntryDto
        {
            Rank = rank,
            TotalEntries = allRepresentatives.Count,
            Time = representative.Time,
            GameId = representative.GameId.ToString(),
            BoardWidth = representative.BoardWidth,
            BoardHeight = representative.BoardHeight,
            Flagged = representative.User.Flagged,
            FlagReason = representative.User.FlagReason,
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
    public Guid Id { get; set; }
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
    public string? FlagReason { get; set; }
}
