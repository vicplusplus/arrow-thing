using System;

/// <summary>
/// One entry in the local leaderboard. Stored in the leaderboard index file (without replay data).
/// The replay itself is stored separately at replays/{gameId}.json.
/// </summary>
[Serializable]
public sealed class LeaderboardEntry
{
    /// <summary>Maps to replay file at replays/{gameId}.json.</summary>
    public string gameId;

    public int seed;
    public int boardWidth;
    public int boardHeight;

    /// <summary>Solve time in seconds.</summary>
    public double solveTime;

    /// <summary>ISO 8601 UTC timestamp of when the game was completed.</summary>
    public string completedAt;

    /// <summary>User-flagged to prevent automatic pruning.</summary>
    public bool isFavorite;

    /// <summary>Application version at the time of play.</summary>
    public string gameVersion;

    /// <summary>Display name at the time the game was completed. May be empty if not logged in.</summary>
    public string displayName;

    /// <summary>Parameterless constructor for deserialization.</summary>
    public LeaderboardEntry() { }

    /// <summary>
    /// Constructs an entry from completed replay data.
    /// </summary>
    public LeaderboardEntry(ReplayData replay, string gameVersion)
    {
        gameId = replay.gameId;
        seed = replay.seed;
        boardWidth = replay.boardWidth;
        boardHeight = replay.boardHeight;
        solveTime = replay.finalTime >= 0 ? replay.finalTime : replay.ComputedSolveElapsed;
        completedAt = DateTime.UtcNow.ToString("O");
        isFavorite = false;
        this.gameVersion = gameVersion ?? "unknown";
    }
}
