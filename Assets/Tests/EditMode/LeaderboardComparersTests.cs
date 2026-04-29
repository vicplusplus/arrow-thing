using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// Unit-level coverage for the named <see cref="LeaderboardComparers"/> instances.
/// The behavioral checks already in <see cref="LeaderboardStoreTests"/> cover
/// <see cref="LeaderboardStore.SortBy"/> end-to-end via the criterion enum;
/// these tests exercise the comparers directly so future refactors that
/// reach for them outside of <see cref="LeaderboardStore.SortBy"/> have a
/// safety net for each comparer in isolation.
/// </summary>
[TestFixture]
public class LeaderboardComparersTests
{
    // ── ForCriterion dispatch ────────────────────────────────────────

    [Test]
    public void ForCriterion_Fastest_ReturnsFastestSingleton()
    {
        Assert.AreSame(
            LeaderboardComparers.Fastest,
            LeaderboardComparers.ForCriterion(SortCriterion.Fastest)
        );
    }

    [Test]
    public void ForCriterion_Biggest_ReturnsBiggestSingleton()
    {
        Assert.AreSame(
            LeaderboardComparers.Biggest,
            LeaderboardComparers.ForCriterion(SortCriterion.Biggest)
        );
    }

    [Test]
    public void ForCriterion_Favorites_ReturnsFavoritesSingleton()
    {
        Assert.AreSame(
            LeaderboardComparers.Favorites,
            LeaderboardComparers.ForCriterion(SortCriterion.Favorites)
        );
    }

    // ── Date tiebreak (older first when primary keys equal) ──────────

    [Test]
    public void Fastest_EqualSolveTime_OlderEntryFirst()
    {
        var a = MakeEntry("a", 10, 10, 5.0, completedAt: "2026-01-01T00:00:00Z");
        var b = MakeEntry("b", 10, 10, 5.0, completedAt: "2026-01-02T00:00:00Z");

        Assert.Less(LeaderboardComparers.Fastest.Compare(a, b), 0);
    }

    [Test]
    public void Biggest_EqualAreaAndTime_OlderEntryFirst()
    {
        var a = MakeEntry("a", 20, 20, 5.0, completedAt: "2026-01-01T00:00:00Z");
        var b = MakeEntry("b", 20, 20, 5.0, completedAt: "2026-01-02T00:00:00Z");

        Assert.Less(LeaderboardComparers.Biggest.Compare(a, b), 0);
    }

    [Test]
    public void Favorites_BothFavoritedEqualEverything_OlderEntryFirst()
    {
        var a = MakeEntry("a", 10, 10, 5.0, isFavorite: true, completedAt: "2026-01-01T00:00:00Z");
        var b = MakeEntry("b", 10, 10, 5.0, isFavorite: true, completedAt: "2026-01-02T00:00:00Z");

        Assert.Less(LeaderboardComparers.Favorites.Compare(a, b), 0);
    }

    // ── Sort sanity per comparer (primary key ordering) ──────────────

    [Test]
    public void Fastest_OrdersByTimeAscending()
    {
        var entries = new List<LeaderboardEntry>
        {
            MakeEntry("g1", 10, 10, 5.0),
            MakeEntry("g2", 10, 10, 2.0),
            MakeEntry("g3", 10, 10, 8.0),
        };
        entries.Sort(LeaderboardComparers.Fastest);

        Assert.AreEqual(new[] { "g2", "g1", "g3" }, ToIds(entries));
    }

    [Test]
    public void Biggest_OrdersByAreaDescendingThenTimeAscending()
    {
        var entries = new List<LeaderboardEntry>
        {
            MakeEntry("small_slow", 10, 10, 8.0),
            MakeEntry("big_slow", 40, 40, 8.0),
            MakeEntry("big_fast", 40, 40, 2.0),
            MakeEntry("medium", 20, 20, 5.0),
        };
        entries.Sort(LeaderboardComparers.Biggest);

        Assert.AreEqual(new[] { "big_fast", "big_slow", "medium", "small_slow" }, ToIds(entries));
    }

    [Test]
    public void Favorites_GroupsFavoritesFirst_ThenAreaDesc_ThenTimeAsc()
    {
        var entries = new List<LeaderboardEntry>
        {
            MakeEntry("nonfav_fast_small", 10, 10, 1.0),
            MakeEntry("fav_big_slow", 40, 40, 8.0, isFavorite: true),
            MakeEntry("fav_small_fast", 10, 10, 2.0, isFavorite: true),
            MakeEntry("nonfav_big_fast", 40, 40, 1.5),
        };
        entries.Sort(LeaderboardComparers.Favorites);

        Assert.AreEqual(
            new[] { "fav_big_slow", "fav_small_fast", "nonfav_big_fast", "nonfav_fast_small" },
            ToIds(entries)
        );
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static LeaderboardEntry MakeEntry(
        string gameId,
        int width,
        int height,
        double solveTime,
        bool isFavorite = false,
        string completedAt = "2026-01-01T00:00:00Z"
    )
    {
        return new LeaderboardEntry
        {
            gameId = gameId,
            seed = 42,
            boardWidth = width,
            boardHeight = height,
            solveTime = solveTime,
            completedAt = completedAt,
            isFavorite = isFavorite,
            gameVersion = "1.0.0",
        };
    }

    private static string[] ToIds(List<LeaderboardEntry> entries)
    {
        var ids = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            ids[i] = entries[i].gameId;
        return ids;
    }
}
