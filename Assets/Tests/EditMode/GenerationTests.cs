using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

[TestFixture]
public class GenerationTests
{
    // Mirrors Board.IsInRay for postcondition checks.
    private static bool IsInRay(Cell target, Cell head, Arrow.Direction direction) =>
        direction switch
        {
            Arrow.Direction.Up => target.X == head.X && target.Y > head.Y,
            Arrow.Direction.Down => target.X == head.X && target.Y < head.Y,
            Arrow.Direction.Right => target.Y == head.Y && target.X > head.X,
            Arrow.Direction.Left => target.Y == head.Y && target.X < head.X,
            _ => false,
        };

    [Test]
    public void FillBoard_SameSeed_ProducesIdenticalBoards()
    {
        var board1 = new Board(6, 6);
        var board2 = new Board(6, 6);
        TestBoardHelper.FillBoard(board1, 5, 42);
        TestBoardHelper.FillBoard(board2, 5, 42);

        Assert.That(board1.Arrows.Count, Is.EqualTo(board2.Arrows.Count));
        for (int i = 0; i < board1.Arrows.Count; i++)
            Assert.That(board1.Arrows[i].Cells, Is.EqualTo(board2.Arrows[i].Cells));
    }

    [Test]
    public void FillBoard_NoCellsOverlap()
    {
        var board = new Board(6, 6);
        TestBoardHelper.FillBoard(board, 5, 7);

        var seen = new HashSet<Cell>();
        foreach (var arrow in board.Arrows)
        foreach (var cell in arrow.Cells)
            Assert.That(
                seen.Add(cell),
                Is.True,
                $"Cell ({cell.X},{cell.Y}) is shared by multiple arrows."
            );
    }

    [Test]
    public void FillBoard_AllCellsWithinBounds()
    {
        var board = new Board(5, 7);
        TestBoardHelper.FillBoard(board, 4, 13);

        foreach (var arrow in board.Arrows)
        foreach (var cell in arrow.Cells)
            Assert.That(
                board.Contains(cell),
                Is.True,
                $"Cell ({cell.X},{cell.Y}) is outside board bounds."
            );
    }

    [Test]
    public void FillBoard_AllArrowsRespectMinLength()
    {
        var board = new Board(6, 6);
        TestBoardHelper.FillBoard(board, 6, 99);

        foreach (var arrow in board.Arrows)
            Assert.That(
                arrow.Cells.Count,
                Is.GreaterThanOrEqualTo(2),
                $"Arrow has only {arrow.Cells.Count} cells, expected >= 2."
            );
    }

    [Test]
    public void FillBoard_NoTailCellInOwnRay()
    {
        var board = new Board(6, 6);
        TestBoardHelper.FillBoard(board, 6, 55);

        foreach (var arrow in board.Arrows)
            for (int i = 1; i < arrow.Cells.Count; i++)
                Assert.That(
                    IsInRay(arrow.Cells[i], arrow.HeadCell, arrow.HeadDirection),
                    Is.False,
                    $"Tail cell ({arrow.Cells[i].X},{arrow.Cells[i].Y}) lies in arrow's own ray."
                );
    }

    [Test]
    public void FillBoard_NoCyclicDependencies()
    {
        // Verify every generated board is fully solvable: repeatedly clear
        // any clearable arrow until no arrows remain or we get stuck.
        for (int seed = 0; seed < 50; seed++)
        {
            var board = new Board(8, 8);
            TestBoardHelper.FillBoard(board, 5, seed);
            int initialCount = board.Arrows.Count;
            Assert.That(initialCount, Is.GreaterThan(0), $"Seed {seed}: no arrows generated.");

            int cleared = 0;
            while (board.Arrows.Count > 0)
            {
                Arrow toClear = null;
                foreach (var arrow in board.Arrows)
                {
                    if (board.IsClearable(arrow))
                    {
                        toClear = arrow;
                        break;
                    }
                }
                Assert.That(
                    toClear,
                    Is.Not.Null,
                    $"Seed {seed}: deadlock with {board.Arrows.Count} arrows remaining (cleared {cleared}/{initialCount})."
                );
                board.RemoveArrow(toClear!);
                cleared++;
            }
        }
    }

    [Test]
    public void FillBoard_RejectsHiddenCycle()
    {
        // Counterexample from the design doc: A masks a B↔C cycle.
        // On a 5-wide board:
        //   A = [(2,0),(2,1)] faces Down, ray exits immediately
        //   B = [(3,0),(4,0)] faces Left, ray → (2,0),(1,0),(0,0)
        //   C = [(1,0),(0,0)] faces Right, ray → (2,0),(3,0),(4,0)
        // After clearing A, B and C block each other — deadlock.
        // The dependency graph must detect this: C depends on A and B (both in its ray),
        // B depends on A and C (both in its ray), forming a B↔C cycle.
        var board = new Board(5, 2);
        var a = new Arrow(new Cell[] { new(2, 0), new(2, 1) });
        var b = new Arrow(new Cell[] { new(3, 0), new(4, 0) });
        var c = new Arrow(new Cell[] { new(1, 0), new(0, 0) });

        board.AddArrow(a);
        board.AddArrow(b);
        board.AddArrow(c);

        // The board has a cycle (B↔C after A is removed).
        // Verify by attempting to clear all arrows:
        int cleared = 0;
        while (board.Arrows.Count > 0)
        {
            Arrow toClear = null;
            foreach (var arrow in board.Arrows)
            {
                if (board.IsClearable(arrow))
                {
                    toClear = arrow;
                    break;
                }
            }
            if (toClear == null)
                break;
            board.RemoveArrow(toClear);
            cleared++;
        }

        // Only A is clearable initially. After removing A, B and C deadlock.
        Assert.That(cleared, Is.EqualTo(1), "Only A should be clearable; B and C should deadlock.");
        Assert.That(board.Arrows.Count, Is.EqualTo(2), "B and C should remain stuck.");
    }

    [Test]
    public void FillBoard_100Iterations_CompletesUnder5Seconds()
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            var board = new Board(10, 10);
            TestBoardHelper.FillBoard(board, 5, i);
        }
        sw.Stop();
        Assert.That(
            sw.ElapsedMilliseconds,
            Is.LessThan(5000),
            $"100x FillBoard(10x10) took {sw.ElapsedMilliseconds}ms, expected < 5000ms."
        );
    }
}
