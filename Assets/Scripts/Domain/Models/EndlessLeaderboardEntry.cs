using System;

/// <summary>
/// One entry in the local endless-mode leaderboard. Stored in the endless index
/// file (without replay data); the replay itself is persisted separately at
/// <c>endless-replays/{gameId}.json.gz</c>.
///
/// Endless ranking is fixed (no user-toggleable sort) — entries are always
/// ordered by <see cref="clears"/> descending with <see cref="durationSeconds"/>
/// ascending as the tiebreak. This mirrors the server's endless leaderboard
/// ordering so the local + global views agree visually.
/// </summary>
[Serializable]
public sealed class EndlessLeaderboardEntry
{
    /// <summary>Maps to replay file at <c>endless-replays/{gameId}.json.gz</c>.</summary>
    public string gameId;

    public int seed;
    public int boardWidth;
    public int boardHeight;

    /// <summary>
    /// Endless tuning bucket the run was played under. Stored so old entries
    /// can be flagged / segregated when tuning constants change.
    /// </summary>
    public int tuningsVersion;

    /// <summary>Total real-arrow clears achieved before topout.</summary>
    public int clears;

    /// <summary>Longest streak of consecutive clears within a combo window.</summary>
    public int longestCombo;

    /// <summary>Sim-time duration of the run in seconds.</summary>
    public double durationSeconds;

    /// <summary>ISO 8601 UTC timestamp of when the run topped out.</summary>
    public string completedAt;

    /// <summary>User-flagged to prevent automatic pruning.</summary>
    public bool isFavorite;

    /// <summary>Application version at the time of play.</summary>
    public string gameVersion;

    /// <summary>Parameterless constructor for deserialization.</summary>
    public EndlessLeaderboardEntry() { }

    /// <summary>
    /// Constructs an entry from a topped-out endless replay payload. Pulls
    /// the optional endless fields off the unified <see cref="ReplayData"/>
    /// and falls back to safe defaults if any are missing (which would be a
    /// recorder bug, but we don't want to throw on persistence-time).
    /// </summary>
    public EndlessLeaderboardEntry(ReplayData replay, string gameVersion)
    {
        gameId = replay.gameId;
        seed = replay.seed;
        boardWidth = replay.boardWidth;
        boardHeight = replay.boardHeight;
        tuningsVersion = replay.tuningsVersion ?? 0;
        clears = replay.clears ?? 0;
        longestCombo = replay.longestCombo ?? 0;
        durationSeconds = replay.durationSeconds ?? 0.0;
        completedAt = DateTime.UtcNow.ToString("O");
        isFavorite = false;
        this.gameVersion = gameVersion ?? replay.gameVersion ?? "unknown";
    }
}
