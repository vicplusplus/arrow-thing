using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

/// <summary>
/// Pure C# leaderboard storage for endless-mode runs. Mirrors
/// <see cref="LeaderboardStore"/> shape but with endless-specific ordering
/// (clears desc, durationSeconds asc tiebreak) and no user-toggleable sort —
/// rank is always fixed.
///
/// Caps: <see cref="MaxEntriesPerConfig"/> per <c>(boardWidth, boardHeight)</c>
/// + <see cref="MaxEntriesGlobal"/> across all configs. Pruning evicts the
/// worst non-favorited entry; favorited entries are exempt. <see cref="AddEntry"/>
/// returns the pruned <c>gameId</c> so the manager can delete the corresponding
/// replay file.
/// </summary>
public sealed class EndlessLeaderboardStore
{
    public const int MaxEntriesPerConfig = 50;
    public const int MaxEntriesGlobal = 500;

    private readonly List<EndlessLeaderboardEntry> _entries = new();

    public IReadOnlyList<EndlessLeaderboardEntry> Entries => _entries;

    /// <summary>
    /// Adds an entry and enforces caps. When a per-config or global cap is exceeded,
    /// the worst non-favorited entry in that group is pruned.
    /// Returns the gameId of any pruned entry (so its replay file can be deleted), or null.
    /// </summary>
    public string AddEntry(EndlessLeaderboardEntry entry)
    {
        // Prune before adding so the new entry is never a pruning candidate
        string pruned = EnforcePerConfigCap(entry.boardWidth, entry.boardHeight);
        pruned ??= EnforceGlobalCap();
        _entries.Add(entry);
        return pruned;
    }

    /// <summary>Returns entries for the given board size, ordered worst → best is not implied — caller sorts.</summary>
    public List<EndlessLeaderboardEntry> GetEntries(int width, int height)
    {
        return _entries.Where(e => e.boardWidth == width && e.boardHeight == height).ToList();
    }

    public List<EndlessLeaderboardEntry> GetAllEntries()
    {
        return new List<EndlessLeaderboardEntry>(_entries);
    }

    public EndlessLeaderboardEntry FindEntry(string gameId)
    {
        return _entries.Find(e => e.gameId == gameId);
    }

    /// <summary>
    /// Returns the personal best entry for the given board size (highest
    /// <see cref="EndlessLeaderboardEntry.clears"/>, with shorter
    /// <see cref="EndlessLeaderboardEntry.durationSeconds"/> winning ties),
    /// or null if no entry exists for that config.
    /// </summary>
    public EndlessLeaderboardEntry GetPersonalBest(int width, int height)
    {
        EndlessLeaderboardEntry best = null;
        foreach (var e in _entries)
        {
            if (e.boardWidth != width || e.boardHeight != height)
                continue;
            if (best == null || CompareScore(e, best) < 0)
                best = e;
        }
        return best;
    }

    public void SetFavorite(string gameId, bool isFavorite)
    {
        var entry = _entries.Find(e => e.gameId == gameId);
        if (entry != null)
            entry.isFavorite = isFavorite;
    }

    /// <summary>
    /// Removes an entry by gameId. Returns true if found and removed.
    /// </summary>
    public bool RemoveEntry(string gameId)
    {
        return _entries.RemoveAll(e => e.gameId == gameId) > 0;
    }

    public string ToJson()
    {
        return JsonConvert.SerializeObject(_entries, Formatting.Indented);
    }

    public static EndlessLeaderboardStore FromJson(string json)
    {
        var store = new EndlessLeaderboardStore();
        if (string.IsNullOrEmpty(json))
            return store;

        var entries = JsonConvert.DeserializeObject<List<EndlessLeaderboardEntry>>(json);
        if (entries != null)
            store._entries.AddRange(entries);
        return store;
    }

    /// <summary>
    /// Compares two endless entries by ranking key: more clears wins, with
    /// shorter duration winning ties. Returns negative if <paramref name="a"/>
    /// is better than <paramref name="b"/>, positive if worse, zero if equal.
    /// </summary>
    public static int CompareScore(EndlessLeaderboardEntry a, EndlessLeaderboardEntry b)
    {
        // Higher clears is better → reverse comparison.
        int byClears = b.clears.CompareTo(a.clears);
        if (byClears != 0)
            return byClears;
        // Shorter duration breaks ties.
        return a.durationSeconds.CompareTo(b.durationSeconds);
    }

    private string EnforcePerConfigCap(int width, int height)
    {
        var configEntries = _entries
            .Where(e => e.boardWidth == width && e.boardHeight == height)
            .ToList();
        if (configEntries.Count < MaxEntriesPerConfig)
            return null;

        return PruneWorst(configEntries);
    }

    private string EnforceGlobalCap()
    {
        if (_entries.Count < MaxEntriesGlobal)
            return null;

        return PruneWorst(_entries);
    }

    /// <summary>
    /// Removes the worst non-favorited entry from the given subset (lowest clears,
    /// longest duration as tiebreak). Returns the pruned entry's gameId, or null
    /// if every candidate is favorited.
    /// </summary>
    private string PruneWorst(List<EndlessLeaderboardEntry> candidates)
    {
        EndlessLeaderboardEntry worst = null;
        foreach (var e in candidates)
        {
            if (e.isFavorite)
                continue;
            if (worst == null || CompareScore(e, worst) > 0)
                worst = e;
        }

        if (worst == null)
            return null; // all favorited, can't prune

        _entries.Remove(worst);
        return worst.gameId;
    }
}
