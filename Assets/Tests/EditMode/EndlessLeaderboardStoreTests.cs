using NUnit.Framework;

[TestFixture]
public class EndlessLeaderboardStoreTests
{
    // ── Sort / scoring ───────────────────────────────────────────────────────

    [Test]
    public void CompareScore_HigherClears_Wins()
    {
        var a = MakeEntry("a", 10, 10, clears: 50, durationSeconds: 120.0);
        var b = MakeEntry("b", 10, 10, clears: 40, durationSeconds: 60.0);

        Assert.Less(EndlessLeaderboardStore.CompareScore(a, b), 0); // a better
        Assert.Greater(EndlessLeaderboardStore.CompareScore(b, a), 0);
    }

    [Test]
    public void CompareScore_EqualClears_ShorterDurationWins()
    {
        var fast = MakeEntry("fast", 10, 10, clears: 30, durationSeconds: 45.0);
        var slow = MakeEntry("slow", 10, 10, clears: 30, durationSeconds: 90.0);

        Assert.Less(EndlessLeaderboardStore.CompareScore(fast, slow), 0);
        Assert.Greater(EndlessLeaderboardStore.CompareScore(slow, fast), 0);
    }

    [Test]
    public void CompareScore_Equal_ReturnsZero()
    {
        var a = MakeEntry("a", 10, 10, clears: 30, durationSeconds: 45.0);
        var b = MakeEntry("b", 10, 10, clears: 30, durationSeconds: 45.0);

        Assert.AreEqual(0, EndlessLeaderboardStore.CompareScore(a, b));
    }

    // ── PersonalBest ─────────────────────────────────────────────────────────

    [Test]
    public void GetPersonalBest_ReturnsHighestClears()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("low", 10, 10, clears: 10, durationSeconds: 30.0));
        store.AddEntry(MakeEntry("high", 10, 10, clears: 25, durationSeconds: 60.0));
        store.AddEntry(MakeEntry("mid", 10, 10, clears: 20, durationSeconds: 30.0));

        var best = store.GetPersonalBest(10, 10);
        Assert.AreEqual("high", best.gameId);
    }

    [Test]
    public void GetPersonalBest_TieBreaksOnDuration()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("slow", 10, 10, clears: 20, durationSeconds: 60.0));
        store.AddEntry(MakeEntry("fast", 10, 10, clears: 20, durationSeconds: 40.0));

        var best = store.GetPersonalBest(10, 10);
        Assert.AreEqual("fast", best.gameId);
    }

    [Test]
    public void GetPersonalBest_ReturnsNull_WhenNoEntries()
    {
        var store = new EndlessLeaderboardStore();
        Assert.IsNull(store.GetPersonalBest(10, 10));
    }

    [Test]
    public void GetPersonalBest_PerConfig()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("small", 5, 5, clears: 10, durationSeconds: 30.0));
        store.AddEntry(MakeEntry("big", 20, 20, clears: 5, durationSeconds: 30.0));

        Assert.AreEqual("small", store.GetPersonalBest(5, 5).gameId);
        Assert.AreEqual("big", store.GetPersonalBest(20, 20).gameId);
    }

    [Test]
    public void AddEntry_WorseScore_DoesNotReplaceBest()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("good", 10, 10, clears: 30, durationSeconds: 45.0));
        store.AddEntry(MakeEntry("worse", 10, 10, clears: 20, durationSeconds: 60.0));

        Assert.AreEqual("good", store.GetPersonalBest(10, 10).gameId);
    }

    [Test]
    public void AddEntry_BetterScore_BecomesNewBest()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("first", 10, 10, clears: 30, durationSeconds: 45.0));
        store.AddEntry(MakeEntry("better", 10, 10, clears: 50, durationSeconds: 90.0));

        Assert.AreEqual("better", store.GetPersonalBest(10, 10).gameId);
    }

    // ── Cap enforcement ──────────────────────────────────────────────────────

    [Test]
    public void PerConfigCap_Plus1_EvictsWorstNonFavorited()
    {
        var store = new EndlessLeaderboardStore();

        // Fill cap with strictly increasing clears: g0 = 1 clear (worst), g49 = 50 clears (best).
        for (int i = 0; i < EndlessLeaderboardStore.MaxEntriesPerConfig; i++)
            store.AddEntry(MakeEntry($"g{i}", 10, 10, clears: i + 1, durationSeconds: 60.0));

        Assert.AreEqual(
            EndlessLeaderboardStore.MaxEntriesPerConfig,
            store.GetEntries(10, 10).Count
        );

        // Adding the 51st entry should evict the worst non-favorited (g0, 1 clear).
        string pruned = store.AddEntry(
            MakeEntry("gNew", 10, 10, clears: 100, durationSeconds: 60.0)
        );

        Assert.AreEqual(
            EndlessLeaderboardStore.MaxEntriesPerConfig,
            store.GetEntries(10, 10).Count
        );
        Assert.AreEqual("g0", pruned);
        Assert.IsNull(store.FindEntry("g0"));
        Assert.IsNotNull(store.FindEntry("gNew"));
    }

    [Test]
    public void PerConfigCap_FavoritedEntries_AreExempt()
    {
        var store = new EndlessLeaderboardStore();

        for (int i = 0; i < EndlessLeaderboardStore.MaxEntriesPerConfig; i++)
            store.AddEntry(MakeEntry($"g{i}", 10, 10, clears: i + 1, durationSeconds: 60.0));

        // Favorite the worst one — it should now be exempt from pruning.
        store.SetFavorite("g0", true);

        // Adding 51st should evict the next-worst (g1, 2 clears) instead.
        string pruned = store.AddEntry(
            MakeEntry("gNew", 10, 10, clears: 100, durationSeconds: 60.0)
        );

        Assert.AreEqual("g1", pruned);
        Assert.IsNotNull(store.FindEntry("g0")); // favorited, still present
        Assert.IsNull(store.FindEntry("g1"));
    }

    [Test]
    public void PerConfigCap_TieBreaksOnDuration()
    {
        var store = new EndlessLeaderboardStore();

        // Same clears across the board → tiebreak on duration. Longer duration is worst.
        for (int i = 0; i < EndlessLeaderboardStore.MaxEntriesPerConfig; i++)
            store.AddEntry(MakeEntry($"g{i}", 10, 10, clears: 10, durationSeconds: 10.0 + i));

        // Worst is g49 (duration = 59.0).
        string pruned = store.AddEntry(MakeEntry("gNew", 10, 10, clears: 50, durationSeconds: 0.0));
        Assert.AreEqual("g49", pruned);
    }

    [Test]
    public void GlobalCap_Plus1_EvictsWorstAcrossAllConfigs()
    {
        var store = new EndlessLeaderboardStore();

        // Spread MaxEntriesGlobal entries across multiple configs so no single
        // config hits its per-config cap. We use 10 configs × 50 entries each
        // (per-config cap is 50 and global cap is 500).
        int perConfig = EndlessLeaderboardStore.MaxEntriesPerConfig; // 50
        int configs = EndlessLeaderboardStore.MaxEntriesGlobal / perConfig; // 10
        for (int c = 0; c < configs; c++)
        {
            int w = 5 + c; // unique config per c (5x5, 6x6, …)
            for (int i = 0; i < perConfig; i++)
            {
                // Make config 0's entries the globally worst (lowest clears),
                // but stay below the per-config cap so nothing prunes yet.
                int clears = c * 100 + i + 1;
                store.AddEntry(
                    MakeEntry($"c{c}-g{i}", w, w, clears: clears, durationSeconds: 60.0)
                );
            }
        }

        Assert.AreEqual(EndlessLeaderboardStore.MaxEntriesGlobal, store.Entries.Count);

        // Add into a new config so per-config cap is not the path that evicts.
        string pruned = store.AddEntry(
            MakeEntry("global-new", 99, 99, clears: 5_000, durationSeconds: 60.0)
        );

        Assert.AreEqual(EndlessLeaderboardStore.MaxEntriesGlobal, store.Entries.Count);
        // Worst overall was c0-g0 (clears = 1, the lowest).
        Assert.AreEqual("c0-g0", pruned);
        Assert.IsNull(store.FindEntry("c0-g0"));
    }

    [Test]
    public void Cap_AllFavorited_ReturnsNullPruned()
    {
        var store = new EndlessLeaderboardStore();
        for (int i = 0; i < EndlessLeaderboardStore.MaxEntriesPerConfig; i++)
        {
            store.AddEntry(MakeEntry($"g{i}", 10, 10, clears: i + 1, durationSeconds: 60.0));
            store.SetFavorite($"g{i}", true);
        }

        string pruned = store.AddEntry(
            MakeEntry("gNew", 10, 10, clears: 100, durationSeconds: 60.0)
        );
        Assert.IsNull(pruned);
        // No eviction → list grew past the cap (intentional: we don't drop
        // the user's favorited history just because they keep playing).
        Assert.AreEqual(
            EndlessLeaderboardStore.MaxEntriesPerConfig + 1,
            store.GetEntries(10, 10).Count
        );
    }

    // ── Favorites / Remove ───────────────────────────────────────────────────

    [Test]
    public void SetFavorite_TogglesFlag()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("g1", 10, 10, clears: 5, durationSeconds: 30.0));

        Assert.IsFalse(store.FindEntry("g1").isFavorite);

        store.SetFavorite("g1", true);
        Assert.IsTrue(store.FindEntry("g1").isFavorite);

        store.SetFavorite("g1", false);
        Assert.IsFalse(store.FindEntry("g1").isFavorite);
    }

    [Test]
    public void SetFavorite_UnknownGameId_NoError()
    {
        var store = new EndlessLeaderboardStore();
        Assert.DoesNotThrow(() => store.SetFavorite("nonexistent", true));
    }

    [Test]
    public void RemoveEntry_RemovesAndReturnsTrue()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("g1", 10, 10, clears: 5, durationSeconds: 30.0));

        Assert.IsTrue(store.RemoveEntry("g1"));
        Assert.AreEqual(0, store.GetEntries(10, 10).Count);
        Assert.IsNull(store.FindEntry("g1"));
    }

    [Test]
    public void RemoveEntry_UnknownId_ReturnsFalse()
    {
        var store = new EndlessLeaderboardStore();
        Assert.IsFalse(store.RemoveEntry("nonexistent"));
    }

    // ── Serialization ────────────────────────────────────────────────────────

    [Test]
    public void ToJson_FromJson_Roundtrips()
    {
        var store = new EndlessLeaderboardStore();
        store.AddEntry(MakeEntry("g1", 10, 10, clears: 12, durationSeconds: 45.5));
        store.AddEntry(
            MakeEntry(
                "g2",
                20,
                20,
                clears: 30,
                durationSeconds: 90.25,
                isFavorite: true,
                longestCombo: 7
            )
        );

        string json = store.ToJson();
        var restored = EndlessLeaderboardStore.FromJson(json);

        Assert.AreEqual(2, restored.Entries.Count);

        var g1 = restored.FindEntry("g1");
        Assert.IsNotNull(g1);
        Assert.AreEqual(10, g1.boardWidth);
        Assert.AreEqual(10, g1.boardHeight);
        Assert.AreEqual(12, g1.clears);
        Assert.AreEqual(45.5, g1.durationSeconds, 1e-9);
        Assert.IsFalse(g1.isFavorite);

        var g2 = restored.FindEntry("g2");
        Assert.IsNotNull(g2);
        Assert.AreEqual(30, g2.clears);
        Assert.AreEqual(7, g2.longestCombo);
        Assert.IsTrue(g2.isFavorite);
    }

    [Test]
    public void FromJson_NullOrEmpty_ReturnsEmptyStore()
    {
        Assert.AreEqual(0, EndlessLeaderboardStore.FromJson(null).Entries.Count);
        Assert.AreEqual(0, EndlessLeaderboardStore.FromJson("").Entries.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static EndlessLeaderboardEntry MakeEntry(
        string gameId,
        int width,
        int height,
        int clears,
        double durationSeconds,
        bool isFavorite = false,
        int longestCombo = 0
    )
    {
        return new EndlessLeaderboardEntry
        {
            gameId = gameId,
            seed = 42,
            boardWidth = width,
            boardHeight = height,
            tuningsVersion = 1,
            clears = clears,
            longestCombo = longestCombo,
            durationSeconds = durationSeconds,
            completedAt = "2026-01-01T00:00:00Z",
            isFavorite = isFavorite,
            gameVersion = "1.0.0",
        };
    }
}
