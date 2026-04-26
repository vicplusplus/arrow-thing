using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// Tests for the endless-mode generator path: centermost-first head selection.
/// Verifies the square-ring fill bias, topout-by-exhaustion behavior, and that
/// all existing invariants (DAG, bounds, min length, no overlap) hold.
/// </summary>
[TestFixture]
public class CentermostGenerationTests
{
    // Integer twice-Chebyshev distance from the board center. Matches the metric
    // used by NativeGeneration.SelectCentermostCandidate.
    private static int TwiceChebyshev(Cell cell, int width, int height)
    {
        int c2x = width - 1;
        int c2y = height - 1;
        int dx = 2 * cell.X - c2x;
        int dy = 2 * cell.Y - c2y;
        if (dx < 0)
            dx = -dx;
        if (dy < 0)
            dy = -dy;
        return dx > dy ? dx : dy;
    }

    [Test]
    public void FillBoardCentermost_SameSeed_ProducesIdenticalBoards()
    {
        var board1 = new Board(8, 8);
        var board2 = new Board(8, 8);
        TestBoardHelper.FillBoardCentermost(board1, 5, 42);
        TestBoardHelper.FillBoardCentermost(board2, 5, 42);

        Assert.That(board1.Arrows.Count, Is.EqualTo(board2.Arrows.Count));
        for (int i = 0; i < board1.Arrows.Count; i++)
            Assert.That(board1.Arrows[i].Cells, Is.EqualTo(board2.Arrows[i].Cells));
    }

    // Drives NativeGeneration.TryGenerateArrow once on an empty board and returns
    // the resulting head cell. Bypasses post-process compaction, which can shuffle
    // board.Arrows order and is not part of the head-selection contract.
    private static Cell FirstGeneratedHead(int width, int height, int seed)
    {
        var state = new NativeGenerationState(width, height);
        state.InitializeCandidates();
        var seedRng = new PortableRandom((uint)seed);
        var rng = new PortableRandom((uint)seedRng.NextInt(1, int.MaxValue));
        bool ok = NativeGeneration.TryGenerateArrow(ref state, 5, ref rng, centermostFirst: true);
        Assert.That(ok, Is.True, "First centermost generation on an empty board must succeed.");
        return new Cell(state.scratchCellsX[0], state.scratchCellsY[0]);
    }

    [Test]
    public void FillBoardCentermost_EvenSize_FirstHeadAtRingOne()
    {
        // On a 6x6 board, the minimum achievable head twice-Chebyshev distance is 1
        // (heads at (2,2), (2,3), (3,2), (3,3)). The first generated arrow must
        // pick one of those.
        for (int seed = 0; seed < 10; seed++)
        {
            Cell head = FirstGeneratedHead(6, 6, seed);
            int d = TwiceChebyshev(head, 6, 6);
            Assert.That(
                d,
                Is.EqualTo(1),
                $"Seed {seed}: first gen head ({head.X},{head.Y}) at ring {d}, expected 1."
            );
        }
    }

    [Test]
    public void FillBoardCentermost_OddSize_FirstHeadAtRingZero()
    {
        // On a 7x7 board, the center cell (3,3) has twice-Chebyshev distance 0.
        // The first generated arrow's head must land there.
        for (int seed = 0; seed < 10; seed++)
        {
            Cell head = FirstGeneratedHead(7, 7, seed);
            int d = TwiceChebyshev(head, 7, 7);
            Assert.That(
                d,
                Is.EqualTo(0),
                $"Seed {seed}: first gen head ({head.X},{head.Y}) at ring {d}, expected 0."
            );
        }
    }

    [Test]
    public void FillBoardCentermost_HasStrongerCenterBiasThanUniform()
    {
        // Aggregate mean head-distance from center should be lower under centermost
        // mode than uniform mode, on the same set of seeds. This validates the
        // square-ring fill bias without overfitting to specific placements.
        const int size = 10;
        const int seeds = 30;

        double centermostMean = MeanHeadDistance(size, seeds, centermost: true);
        double uniformMean = MeanHeadDistance(size, seeds, centermost: false);

        Assert.That(
            centermostMean,
            Is.LessThan(uniformMean),
            $"Centermost mean head-distance {centermostMean:F2} should be less than uniform {uniformMean:F2}."
        );
    }

    private static double MeanHeadDistance(int size, int seeds, bool centermost)
    {
        long totalDist = 0;
        long totalHeads = 0;
        for (int seed = 0; seed < seeds; seed++)
        {
            var board = new Board(size, size);
            if (centermost)
                TestBoardHelper.FillBoardCentermost(board, 5, seed);
            else
                TestBoardHelper.FillBoard(board, 5, seed);

            foreach (var arrow in board.Arrows)
            {
                totalDist += TwiceChebyshev(arrow.HeadCell, size, size);
                totalHeads++;
            }
        }
        return (double)totalDist / totalHeads;
    }

    [Test]
    public void FillBoardCentermost_AllInvariantsHold()
    {
        // Board invariants that must hold under centermost mode too:
        //   - all cells in bounds
        //   - no cell overlap between arrows
        //   - every arrow has >= 2 cells
        //   - DAG: the board is fully solvable
        for (int seed = 0; seed < 20; seed++)
        {
            var board = new Board(8, 8);
            TestBoardHelper.FillBoardCentermost(board, 5, seed);

            var seen = new HashSet<Cell>();
            foreach (var arrow in board.Arrows)
            {
                Assert.That(
                    arrow.Cells.Count,
                    Is.GreaterThanOrEqualTo(2),
                    $"Seed {seed}: arrow has only {arrow.Cells.Count} cells."
                );
                foreach (var cell in arrow.Cells)
                {
                    Assert.That(
                        board.Contains(cell),
                        Is.True,
                        $"Seed {seed}: cell ({cell.X},{cell.Y}) out of bounds."
                    );
                    Assert.That(
                        seen.Add(cell),
                        Is.True,
                        $"Seed {seed}: cell ({cell.X},{cell.Y}) shared by multiple arrows."
                    );
                }
            }

            // Drain the board via repeated clearability checks. Deadlock => cycle.
            int initialCount = board.Arrows.Count;
            Assert.That(initialCount, Is.GreaterThan(0), $"Seed {seed}: no arrows generated.");
            int cleared = 0;
            while (board.Arrows.Count > 0)
            {
                Arrow toClear = null;
                foreach (var arrow in board.Arrows)
                    if (board.IsClearable(arrow))
                    {
                        toClear = arrow;
                        break;
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
    public void TryGenerateArrowCentermost_ReturnsFalseWhenBoardSaturated()
    {
        // Drive the native generator directly: fill as much as possible in centermost
        // mode, then confirm the next TryGenerateArrow call returns false (topout
        // = generator exhaustion, i.e. no candidate can produce a valid arrow).
        var state = new NativeGenerationState(6, 6);
        state.InitializeCandidates();
        var seedRng = new PortableRandom(123u);
        var rng = new PortableRandom((uint)seedRng.NextInt(1, int.MaxValue));

        int placed = 0;
        while (NativeGeneration.TryGenerateArrow(ref state, 5, ref rng, centermostFirst: true))
            placed++;

        Assert.That(placed, Is.GreaterThan(0), "Should place at least one arrow on an empty 6x6.");
        Assert.That(
            NativeGeneration.TryGenerateArrow(ref state, 5, ref rng, centermostFirst: true),
            Is.False,
            "After saturation, TryGenerateArrow must return false (topout)."
        );
    }
}
