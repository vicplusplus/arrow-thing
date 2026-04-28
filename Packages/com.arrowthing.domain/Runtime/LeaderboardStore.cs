using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

/// <summary>
/// Pure C# leaderboard storage. Manages entries with per-config and global caps.
/// Favorited entries are exempt from automatic pruning.
/// </summary>
public sealed class LeaderboardStore
{
    public const int MaxEntriesPerConfig = 50;
    public const int MaxEntriesGlobal = 500;

    private readonly List<LeaderboardEntry> _entries = new();

    public IReadOnlyList<LeaderboardEntry> Entries => _entries;

    /// <summary>
    /// Adds an entry and enforces caps. When a per-config or global cap is exceeded,
    /// the slowest non-favorited entry in that group is pruned.
    /// Returns the gameId of any pruned entry (so its replay file can be deleted), or null.
    /// </summary>
    public string AddEntry(LeaderboardEntry entry)
    {
        // Prune before adding so the new entry is never a pruning candidate
        string pruned = EnforcePerConfigCap(entry.boardWidth, entry.boardHeight);
        pruned ??= EnforceGlobalCap();
        _entries.Add(entry);
        return pruned;
    }

    public List<LeaderboardEntry> GetEntries(int width, int height)
    {
        return _entries.Where(e => e.boardWidth == width && e.boardHeight == height).ToList();
    }

    public List<LeaderboardEntry> GetAllEntries()
    {
        return new List<LeaderboardEntry>(_entries);
    }

    public LeaderboardEntry FindEntry(string gameId)
    {
        return _entries.Find(e => e.gameId == gameId);
    }

    /// <summary>Returns the fastest entry for the given board size, or null if none exist.</summary>
    public LeaderboardEntry GetPersonalBest(int width, int height)
    {
        LeaderboardEntry best = null;
        foreach (var e in _entries)
        {
            if (e.boardWidth == width && e.boardHeight == height)
            {
                if (best == null || e.solveTime < best.solveTime)
                    best = e;
            }
        }
        return best;
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> entries for the given board size,
    /// centered around <paramref name="targetTime"/> when sorted by fastest time.
    /// Useful for showing entries near a specific time.
    /// </summary>
    public List<LeaderboardEntry> GetNeighborEntries(
        int width,
        int height,
        double targetTime,
        int count
    )
    {
        var sorted = GetEntries(width, height);
        sorted.Sort((a, b) => a.solveTime.CompareTo(b.solveTime));

        if (sorted.Count <= count)
            return sorted;

        // Find the index where targetTime would be inserted
        int insertIndex = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].solveTime > targetTime)
                break;
            insertIndex = i + 1;
        }

        // Center the window around the insert point
        int halfBelow = count / 2;
        int start = insertIndex - halfBelow;

        // Clamp to valid range
        if (start < 0)
            start = 0;
        if (start + count > sorted.Count)
            start = sorted.Count - count;

        return sorted.GetRange(start, count);
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

    /// <summary>
    /// Removes all non-favorited entries. Returns the gameIds of removed entries
    /// so their replay files can be deleted.
    /// </summary>
    public List<string> RemoveAllNonFavorited()
    {
        var removed = _entries.Where(e => !e.isFavorite).Select(e => e.gameId).ToList();
        _entries.RemoveAll(e => !e.isFavorite);
        return removed;
    }

    /// <summary>
    /// Returns a sorted copy of the given entries by the specified criterion.
    /// </summary>
    public static List<LeaderboardEntry> SortBy(
        List<LeaderboardEntry> entries,
        SortCriterion criterion
    )
    {
        var sorted = new List<LeaderboardEntry>(entries);
        switch (criterion)
        {
            case SortCriterion.Fastest:
                sorted.Sort(
                    (a, b) =>
                    {
                        int cmp = a.solveTime.CompareTo(b.solveTime);
                        return cmp != 0 ? cmp : DateTiebreak(a, b);
                    }
                );
                break;
            case SortCriterion.Biggest:
                sorted.Sort(
                    (a, b) =>
                    {
                        int areaA = a.boardWidth * a.boardHeight;
                        int areaB = b.boardWidth * b.boardHeight;
                        int cmp = areaB.CompareTo(areaA); // descending
                        if (cmp != 0)
                            return cmp;
                        int timeCmp = a.solveTime.CompareTo(b.solveTime);
                        return timeCmp != 0 ? timeCmp : DateTiebreak(a, b);
                    }
                );
                break;
            case SortCriterion.Favorites:
                sorted.Sort(
                    (a, b) =>
                    {
                        int favCmp = b.isFavorite.CompareTo(a.isFavorite);
                        if (favCmp != 0)
                            return favCmp;
                        // Size descending (no-op on size-filtered tabs)
                        int areaA = a.boardWidth * a.boardHeight;
                        int areaB = b.boardWidth * b.boardHeight;
                        int sizeCmp = areaB.CompareTo(areaA);
                        if (sizeCmp != 0)
                            return sizeCmp;
                        int timeCmp = a.solveTime.CompareTo(b.solveTime);
                        return timeCmp != 0 ? timeCmp : DateTiebreak(a, b);
                    }
                );
                break;
        }
        return sorted;
    }

    /// <summary>Tiebreaker: older entries first (ascending by completedAt).</summary>
    private static int DateTiebreak(LeaderboardEntry a, LeaderboardEntry b)
    {
        return string.Compare(a.completedAt, b.completedAt, System.StringComparison.Ordinal);
    }

    public string ToJson()
    {
        return JsonConvert.SerializeObject(_entries, Formatting.Indented);
    }

    public static LeaderboardStore FromJson(string json)
    {
        var store = new LeaderboardStore();
        if (string.IsNullOrEmpty(json))
            return store;

        var entries = JsonConvert.DeserializeObject<List<LeaderboardEntry>>(json);
        if (entries != null)
            store._entries.AddRange(entries);
        return store;
    }

    private string EnforcePerConfigCap(int width, int height)
    {
        var configEntries = _entries
            .Where(e => e.boardWidth == width && e.boardHeight == height)
            .ToList();
        if (configEntries.Count < MaxEntriesPerConfig)
            return null;

        return PruneSlowest(configEntries);
    }

    private string EnforceGlobalCap()
    {
        if (_entries.Count < MaxEntriesGlobal)
            return null;

        return PruneSlowest(_entries);
    }

    /// <summary>
    /// Removes the slowest non-favorited entry from the given subset.
    /// Returns the pruned entry's gameId, or null if all are favorited.
    /// </summary>
    private string PruneSlowest(List<LeaderboardEntry> candidates)
    {
        LeaderboardEntry slowest = null;
        foreach (var e in candidates)
        {
            if (e.isFavorite)
                continue;
            if (slowest == null || e.solveTime > slowest.solveTime)
                slowest = e;
        }

        if (slowest == null)
            return null; // all favorited, can't prune

        _entries.Remove(slowest);
        return slowest.gameId;
    }
}

public enum SortCriterion
{
    Fastest,
    Biggest,
    Favorites,
}
