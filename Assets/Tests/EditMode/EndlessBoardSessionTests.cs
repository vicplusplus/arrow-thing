using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

/// <summary>
/// Tests for the endless-mode session: ghost spawn/commit/drop, real arrow
/// clearing through the session, cycle prevention across spawn cycles, and
/// topout detection.
/// </summary>
[TestFixture]
public class EndlessBoardSessionTests
{
    private static Board BuildFilledBoard(int w, int h, int maxLength, int seed)
    {
        var board = new Board(w, h);
        var e = BoardGeneration.FillBoardIncremental(board, maxLength, seed);
        while (e.MoveNext()) { }
        return board;
    }

    // Saturates a board then clears ~25% of arrows to leave spawn room.
    // Mirrors realistic mid-game endless state. A freshly-filled board is
    // typically saturated to topout; these tests need headroom for new
    // ghosts.
    private static Board BuildMidGameBoard(int w, int h, int maxLength, int seed)
    {
        var board = BuildFilledBoard(w, h, maxLength, seed);
        int target = board.Arrows.Count / 4;
        int cleared = 0;
        while (cleared < target)
        {
            Arrow toClear = null;
            foreach (var a in board.Arrows)
                if (board.IsClearable(a))
                {
                    toClear = a;
                    break;
                }
            if (toClear == null)
                break;
            board.RemoveArrow(toClear);
            cleared++;
        }
        return board;
    }

    // Drains the board via repeated clearability checks. Returns (cleared, stuck).
    private static (int cleared, int stuck) DrainBoard(Board board)
    {
        int cleared = 0;
        while (board.Arrows.Count > 0)
        {
            Arrow toClear = null;
            foreach (var a in board.Arrows)
                if (board.IsClearable(a))
                {
                    toClear = a;
                    break;
                }
            if (toClear == null)
                return (cleared, board.Arrows.Count);
            board.RemoveArrow(toClear);
            cleared++;
        }
        return (cleared, 0);
    }

    [Test]
    public void Session_Init_PreservesBoardAndArrowCount()
    {
        var board = BuildFilledBoard(8, 8, 5, 42);
        int originalCount = board.Arrows.Count;
        var session = new EndlessBoardSession(board, 5, 123);

        Assert.That(session.Board, Is.SameAs(board));
        Assert.That(board.Arrows.Count, Is.EqualTo(originalCount));
    }

    [Test]
    public void TrySpawnPending_OnMidGameBoard_ReturnsNonNullAndAddsToPending()
    {
        var board = BuildMidGameBoard(8, 8, 5, 7);
        int before = board.Arrows.Count;
        var session = new EndlessBoardSession(board, 5, 99);

        var pending = session.TrySpawnPending(commitAt: 3.0f);

        Assert.That(pending, Is.Not.Null);
        Assert.That(session.PendingArrows.Count, Is.EqualTo(1));
        Assert.That(session.PendingArrows[0], Is.SameAs(pending));
        Assert.That(
            board.Arrows.Count,
            Is.EqualTo(before),
            "ghost must not be added to Board on spawn"
        );
        Assert.That(board.Arrows.Contains(pending.Arrow), Is.False);
        Assert.That(pending.CommitAt, Is.EqualTo(3.0f));
    }

    [Test]
    public void CommitPending_MovesArrowToBoardAndEmptiesPending()
    {
        var board = BuildMidGameBoard(8, 8, 5, 11);
        int before = board.Arrows.Count;
        var session = new EndlessBoardSession(board, 5, 22);

        var pending = session.TrySpawnPending(5.0f);
        Assume.That(pending, Is.Not.Null);
        session.CommitPending(pending);

        Assert.That(session.PendingArrows.Count, Is.EqualTo(0));
        Assert.That(board.Arrows.Count, Is.EqualTo(before + 1));
        Assert.That(board.Arrows.Contains(pending.Arrow), Is.True);
    }

    [Test]
    public void DropPending_RemovesFromPendingWithoutTouchingBoard()
    {
        var board = BuildMidGameBoard(8, 8, 5, 23);
        int before = board.Arrows.Count;
        var session = new EndlessBoardSession(board, 5, 55);

        var pending = session.TrySpawnPending(2.0f);
        Assume.That(pending, Is.Not.Null);
        session.DropPending(pending);

        Assert.That(session.PendingArrows.Count, Is.EqualTo(0));
        Assert.That(board.Arrows.Count, Is.EqualTo(before));
        Assert.That(board.Arrows.Contains(pending.Arrow), Is.False);
    }

    [Test]
    public void ClearRealArrow_RemovesFromBoardAndOpensCellsForReuse()
    {
        var board = BuildMidGameBoard(8, 8, 5, 19);
        var session = new EndlessBoardSession(board, 5, 44);
        int beforeCount = board.Arrows.Count;

        Arrow toClear = null;
        foreach (var a in board.Arrows)
            if (board.IsClearable(a))
            {
                toClear = a;
                break;
            }
        Assume.That(toClear, Is.Not.Null, "expected a clearable arrow on a fresh mid-game board");

        var clearedCells = toClear.Cells.ToList();
        session.ClearRealArrow(toClear);

        Assert.That(board.Arrows.Count, Is.EqualTo(beforeCount - 1));
        Assert.That(board.Arrows.Contains(toClear), Is.False);

        // Spawn a few ghosts and confirm the cleared cells can be reoccupied,
        // proving the native state also released them (not just Board).
        var reoccupied = new HashSet<Cell>();
        for (int i = 0; i < 20; i++)
        {
            var p = session.TrySpawnPending(i);
            if (p == null)
                break;
            foreach (var c in p.Arrow.Cells)
                if (clearedCells.Contains(c))
                    reoccupied.Add(c);
        }
        Assert.That(
            reoccupied.Count,
            Is.GreaterThan(0),
            "expected at least one cleared cell to be reoccupied by a subsequent ghost"
        );
    }

    [Test]
    public void SpawnAndCommit_PreservesDAGAcrossManyCycles()
    {
        for (int seed = 0; seed < 10; seed++)
        {
            var board = BuildMidGameBoard(8, 8, 5, 37 + seed);
            var session = new EndlessBoardSession(board, 5, 88 + seed);

            int committed = 0;
            for (int i = 0; i < 30; i++)
            {
                var p = session.TrySpawnPending(i);
                if (p == null)
                    break;
                session.CommitPending(p);
                committed++;
            }
            Assume.That(committed, Is.GreaterThan(0), $"seed {seed}: expected at least one commit");

            int total = board.Arrows.Count;
            var (cleared, stuck) = DrainBoard(board);
            Assert.That(
                stuck,
                Is.EqualTo(0),
                $"seed {seed}: deadlock after {committed} commits; cleared {cleared}/{total}."
            );
        }
    }

    [Test]
    public void TrySpawnPending_OnFullBoard_EventuallyReturnsNull()
    {
        // On a saturated board, no more arrows can be placed —
        // TrySpawnPending must return null within a bounded number of attempts.
        var board = BuildFilledBoard(6, 6, 5, 1);
        var session = new EndlessBoardSession(board, 5, 2);

        var p = session.TrySpawnPending(0f);
        int guard = 0;
        while (p != null && guard++ < 50)
            p = session.TrySpawnPending(guard);

        Assert.That(p, Is.Null, "expected topout within 50 spawn attempts on a saturated 6x6");
    }

    [Test]
    public void TrySpawnPending_GhostCellsNotInBoardOccupancy()
    {
        var board = BuildMidGameBoard(8, 8, 5, 41);
        var session = new EndlessBoardSession(board, 5, 100);

        var p = session.TrySpawnPending(1.0f);
        Assume.That(p, Is.Not.Null);

        foreach (var c in p.Arrow.Cells)
            Assert.That(
                board.GetArrowAt(c),
                Is.Not.EqualTo(p.Arrow),
                $"Ghost cell ({c.X},{c.Y}) must not be in Board._occupancy as the ghost arrow."
            );
    }
}
