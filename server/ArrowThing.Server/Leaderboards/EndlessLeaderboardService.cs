using ArrowThing.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Leaderboards;

/// <summary>
/// Parallel of <see cref="LeaderboardService"/> for endless mode. Same
/// public shape (top-N + global "all" + per-player /me lookups) but
/// ranks by Clears desc with DurationSeconds asc as the tiebreak,
/// instead of classic's pure Time asc.
///
/// <para>Cache: shares <see cref="EndlessLeaderboardCache"/>, a sibling
/// of <see cref="LeaderboardCache"/> keyed identically by (width, height)
/// — (0, 0) reserved for the global "all" query.</para>
/// </summary>
public class EndlessLeaderboardService
{
    private readonly AppDbContext _db;
    private readonly EndlessLeaderboardCache _cache;

    public EndlessLeaderboardService(AppDbContext db, EndlessLeaderboardCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<EndlessLeaderboardResponse> GetTopEntriesAsync(int width, int height)
    {
        var cached = _cache.Get(width, height);
        if (cached != null)
            return cached;

        var totalEntries = await _db.EndlessScores.CountAsync(s =>
            s.BoardWidth == width && s.BoardHeight == height && !s.User.Flagged
        );

        var entries = await _db
            .EndlessScores.Where(s =>
                s.BoardWidth == width && s.BoardHeight == height && !s.User.Flagged
            )
            .OrderByDescending(s => s.Clears)
            .ThenBy(s => s.DurationSeconds)
            .Take(50)
            .Select(s => new EndlessLeaderboardEntryDto
            {
                DisplayName = s.User.DisplayName,
                Clears = s.Clears,
                LongestCombo = s.LongestCombo,
                DurationSeconds = s.DurationSeconds,
                GameId = s.GameId.ToString(),
            })
            .ToListAsync();

        for (int i = 0; i < entries.Count; i++)
            entries[i].Rank = i + 1;

        var response = new EndlessLeaderboardResponse
        {
            TotalEntries = totalEntries,
            Entries = entries,
        };
        _cache.Set(width, height, response);
        return response;
    }

    /// <summary>
    /// Global top-50 across all configs. "Best" across configs ranks by
    /// Clears desc, then DurationSeconds asc, then board area desc as a
    /// final tiebreak. Documented on the endpoint.
    /// </summary>
    public async Task<EndlessLeaderboardResponse> GetTopEntriesAllAsync()
    {
        var cached = _cache.Get(0, 0);
        if (cached != null)
            return cached;

        var rows = await _db
            .Database.SqlQueryRaw<EndlessAllLeaderboardRow>(
                """
                SELECT sub."DisplayName", sub."Clears", sub."LongestCombo",
                       sub."DurationSeconds", sub."GameId",
                       sub."BoardWidth", sub."BoardHeight"
                FROM (
                    SELECT DISTINCT ON (s."UserId")
                        u."DisplayName", s."Clears", s."LongestCombo",
                        s."DurationSeconds", s."GameId"::text AS "GameId",
                        s."BoardWidth", s."BoardHeight"
                    FROM "EndlessScores" s
                    INNER JOIN "Users" u ON s."UserId" = u."Id"
                    WHERE NOT u."Flagged"
                    ORDER BY s."UserId",
                             s."Clears" DESC,
                             s."DurationSeconds" ASC,
                             (s."BoardWidth" * s."BoardHeight") DESC
                ) sub
                ORDER BY sub."Clears" DESC,
                         sub."DurationSeconds" ASC,
                         (sub."BoardWidth" * sub."BoardHeight") DESC
                LIMIT 50
                """
            )
            .ToListAsync();

        var totalCount = await _db
            .Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(DISTINCT s."UserId")::int AS "Value"
                FROM "EndlessScores" s
                INNER JOIN "Users" u ON s."UserId" = u."Id"
                WHERE NOT u."Flagged"
                """
            )
            .SingleAsync();

        var entries = rows.Select(
                (r, i) =>
                    new EndlessLeaderboardEntryDto
                    {
                        Rank = i + 1,
                        DisplayName = r.DisplayName,
                        Clears = r.Clears,
                        LongestCombo = r.LongestCombo,
                        DurationSeconds = r.DurationSeconds,
                        GameId = r.GameId,
                        BoardWidth = r.BoardWidth,
                        BoardHeight = r.BoardHeight,
                    }
            )
            .ToList();

        var response = new EndlessLeaderboardResponse
        {
            TotalEntries = totalCount,
            Entries = entries,
        };
        _cache.Set(0, 0, response);
        return response;
    }

    public async Task<EndlessPlayerEntryDto?> GetPlayerEntryAsync(
        Guid userId,
        int width,
        int height
    )
    {
        var score = await _db
            .EndlessScores.Include(s => s.User)
            .FirstOrDefaultAsync(s =>
                s.UserId == userId && s.BoardWidth == width && s.BoardHeight == height
            );
        if (score == null)
            return null;

        // Two FILTERed aggregates over the same per-config scan: the
        // total and the count strictly better than this player.
        var counts = await _db
            .Database.SqlQueryRaw<RankCountsRow>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE NOT u."Flagged")::int AS "Total",
                    COUNT(*) FILTER (
                        WHERE NOT u."Flagged"
                          AND (s."Clears" > {2}
                               OR (s."Clears" = {2} AND s."DurationSeconds" < {3}))
                    )::int AS "Beats"
                FROM "EndlessScores" s
                INNER JOIN "Users" u ON s."UserId" = u."Id"
                WHERE s."BoardWidth" = {0} AND s."BoardHeight" = {1}
                """,
                width,
                height,
                score.Clears,
                score.DurationSeconds
            )
            .SingleAsync();

        return new EndlessPlayerEntryDto
        {
            Rank = counts.Beats + 1,
            TotalEntries = counts.Total,
            Clears = score.Clears,
            LongestCombo = score.LongestCombo,
            DurationSeconds = score.DurationSeconds,
            GameId = score.GameId.ToString(),
            Flagged = score.User.Flagged,
        };
    }

    public async Task<EndlessPlayerEntryDto?> GetPlayerEntryAllAsync(Guid userId)
    {
        // Player's representative score: best across their own configs by
        // the same rank rule (clears desc, duration asc, area desc).
        var representative = await _db
            .EndlessScores.Include(s => s.User)
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.Clears)
            .ThenBy(s => s.DurationSeconds)
            .ThenByDescending(s => s.BoardWidth * s.BoardHeight)
            .FirstOrDefaultAsync();
        if (representative == null)
            return null;

        var repArea = representative.BoardWidth * representative.BoardHeight;
        var counts = await _db
            .Database.SqlQueryRaw<RankCountsRow>(
                """
                WITH representatives AS (
                    SELECT DISTINCT ON (s."UserId")
                        s."Clears", s."DurationSeconds",
                        (s."BoardWidth" * s."BoardHeight") AS area
                    FROM "EndlessScores" s
                    INNER JOIN "Users" u ON s."UserId" = u."Id"
                    WHERE NOT u."Flagged"
                    ORDER BY s."UserId",
                             s."Clears" DESC,
                             s."DurationSeconds" ASC,
                             (s."BoardWidth" * s."BoardHeight") DESC
                )
                SELECT
                    COUNT(*)::int AS "Total",
                    COUNT(*) FILTER (
                        WHERE "Clears" > {0}
                           OR ("Clears" = {0} AND "DurationSeconds" < {1})
                           OR ("Clears" = {0} AND "DurationSeconds" = {1} AND area > {2})
                    )::int AS "Beats"
                FROM representatives
                """,
                representative.Clears,
                representative.DurationSeconds,
                repArea
            )
            .SingleAsync();

        return new EndlessPlayerEntryDto
        {
            Rank = counts.Beats + 1,
            TotalEntries = counts.Total,
            Clears = representative.Clears,
            LongestCombo = representative.LongestCombo,
            DurationSeconds = representative.DurationSeconds,
            GameId = representative.GameId.ToString(),
            BoardWidth = representative.BoardWidth,
            BoardHeight = representative.BoardHeight,
            Flagged = representative.User.Flagged,
        };
    }

    private class RankCountsRow
    {
        public int Total { get; set; }
        public int Beats { get; set; }
    }
}

public class EndlessLeaderboardResponse
{
    public int TotalEntries { get; set; }
    public List<EndlessLeaderboardEntryDto> Entries { get; set; } = new();
}

public class EndlessLeaderboardEntryDto
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

public class EndlessPlayerEntryDto
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

public class EndlessAllLeaderboardRow
{
    public string DisplayName { get; set; } = "";
    public int Clears { get; set; }
    public int LongestCombo { get; set; }
    public double DurationSeconds { get; set; }
    public string GameId { get; set; } = "";
    public int BoardWidth { get; set; }
    public int BoardHeight { get; set; }
}
