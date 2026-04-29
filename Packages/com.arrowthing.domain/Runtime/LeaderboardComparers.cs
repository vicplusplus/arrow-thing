using System;
using System.Collections.Generic;

/// <summary>
/// Named, reusable comparers that implement each <see cref="SortCriterion"/>
/// for <see cref="LeaderboardEntry"/>. Sort modes are pure-data filters —
/// the controller's "active sort" enum just picks which comparer drives
/// the render path. Keeping the comparers as named types (instead of
/// lambdas inlined in a switch) lets them be unit-tested in isolation,
/// composed into other ordering operations, and referenced by identity
/// where a controller policy needs to special-case "is the favorites
/// comparer active right now?".
///
/// All comparers tiebreak deterministically by <see cref="LeaderboardEntry.completedAt"/>
/// (older first) so that two entries that compare equal on the primary
/// key always render in a stable order across runs.
/// </summary>
public static class LeaderboardComparers
{
    /// <summary>Solve time ascending (faster first), then date tiebreak.</summary>
    public static IComparer<LeaderboardEntry> Fastest { get; } = new FastestComparer();

    /// <summary>Board area descending (bigger first), then time ascending, then date.</summary>
    public static IComparer<LeaderboardEntry> Biggest { get; } = new BiggestComparer();

    /// <summary>
    /// Favorites first (true before false), then area descending (no-op on
    /// size-filtered tabs but used on the cross-size All tab), then time
    /// ascending, then date.
    /// </summary>
    public static IComparer<LeaderboardEntry> Favorites { get; } = new FavoritesComparer();

    /// <summary>Returns the singleton comparer matching <paramref name="criterion"/>.</summary>
    public static IComparer<LeaderboardEntry> ForCriterion(SortCriterion criterion)
    {
        switch (criterion)
        {
            case SortCriterion.Fastest:
                return Fastest;
            case SortCriterion.Biggest:
                return Biggest;
            case SortCriterion.Favorites:
                return Favorites;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(criterion),
                    criterion,
                    "Unknown sort criterion."
                );
        }
    }

    private static int DateTiebreak(LeaderboardEntry a, LeaderboardEntry b) =>
        string.Compare(a.completedAt, b.completedAt, StringComparison.Ordinal);

    private sealed class FastestComparer : IComparer<LeaderboardEntry>
    {
        public int Compare(LeaderboardEntry a, LeaderboardEntry b)
        {
            int cmp = a.solveTime.CompareTo(b.solveTime);
            return cmp != 0 ? cmp : DateTiebreak(a, b);
        }
    }

    private sealed class BiggestComparer : IComparer<LeaderboardEntry>
    {
        public int Compare(LeaderboardEntry a, LeaderboardEntry b)
        {
            int areaA = a.boardWidth * a.boardHeight;
            int areaB = b.boardWidth * b.boardHeight;
            int cmp = areaB.CompareTo(areaA); // descending
            if (cmp != 0)
                return cmp;
            int timeCmp = a.solveTime.CompareTo(b.solveTime);
            return timeCmp != 0 ? timeCmp : DateTiebreak(a, b);
        }
    }

    private sealed class FavoritesComparer : IComparer<LeaderboardEntry>
    {
        public int Compare(LeaderboardEntry a, LeaderboardEntry b)
        {
            int favCmp = b.isFavorite.CompareTo(a.isFavorite);
            if (favCmp != 0)
                return favCmp;
            int areaA = a.boardWidth * a.boardHeight;
            int areaB = b.boardWidth * b.boardHeight;
            int sizeCmp = areaB.CompareTo(areaA);
            if (sizeCmp != 0)
                return sizeCmp;
            int timeCmp = a.solveTime.CompareTo(b.solveTime);
            return timeCmp != 0 ? timeCmp : DateTiebreak(a, b);
        }
    }
}
