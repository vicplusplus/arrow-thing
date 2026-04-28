using System;
using System.Collections;
using System.Collections.Generic;

public static class BoardGeneration
{
    private const int MinArrowLength = 2;

    /// <summary>
    /// Incremental version of board filling using the portable NativeGeneration kernel.
    /// Places as many arrows as possible, yielding after each arrow for progress.
    /// Yields <see cref="GenerationPhase"/> values between phases.
    /// </summary>
    public static IEnumerator FillBoardIncremental(Board board, int maxLength, int seed)
    {
        int maxPossibleArrows = board.Width * board.Height / 2;

        // Allocate managed generation state. No try/finally needed — the arrays
        // are GC-managed and will be reclaimed normally if the iterator is
        // abandoned (e.g. when GenerateBoard is cancelled).
        var state = new NativeGenerationState(board.Width, board.Height);
        state.InitializeCandidates();
        board.InitialCandidateCount = state.candidateCount;

        // Derive the generation RNG seed portably using PortableRandom itself,
        // avoiding System.Random which differs between Mono (Unity) and .NET.
        var seedRng = new PortableRandom((uint)seed);
        var rng = new PortableRandom((uint)seedRng.NextInt(1, int.MaxValue));
        int created = 0;

        while (created < maxPossibleArrows && state.candidateCount > 0)
        {
            if (!NativeGeneration.TryGenerateArrow(ref state, maxLength, ref rng))
                break;

            // Extract arrow from scratch buffers
            var cells = new List<Cell>(state.lastArrowCellCount);
            for (int i = 0; i < state.lastArrowCellCount; i++)
                cells.Add(new Cell(state.scratchCellsX[i], state.scratchCellsY[i]));
            var arrow = new Arrow(cells);
            arrow._generationIndex = created;

            board._arrows.Add(arrow);
            board._arrowSet.Add(arrow);
            board.OccupiedCellCount += cells.Count;
            board._nativeRemainingCandidates = state.candidateCount;
            created++;
            yield return null;
        }

        // Copy generation state to Board's managed fields for compaction
        board.InitializeFromNativeGeneration(ref state);

        // Compaction phase: merge trivial collinear same-direction chains
        yield return GenerationPhase.Compacting;
        var compactor = CompactBoardInPlace(board);
        while (compactor.MoveNext())
            yield return compactor.Current;

        // Finalization phase: build HashSet dependency graph
        yield return GenerationPhase.Finalizing;
        var finalizer = board.FinalizeGenerationIncremental();
        while (finalizer.MoveNext())
            yield return finalizer.Current;
    }

    /// <summary>
    /// Post-process compaction: iteratively merges trivial collinear same-direction
    /// chains in-place on the board using RemoveArrowForGeneration/AddArrowForGeneration.
    /// Yields the cumulative merge count after each merge for smooth progress tracking.
    /// </summary>
    private static IEnumerator<int> CompactBoardInPlace(Board board)
    {
        int mergeCount = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            var arrows = new List<Arrow>(board.Arrows);

            foreach (Arrow dependent in arrows)
            {
                // Skip arrows merged earlier in this pass
                if (dependent._generationIndex < 0)
                    continue;

                (int dx, int dy) = Arrow.GetDirectionStep(dependent.HeadDirection);
                int cx = dependent.HeadCell.X + dx,
                    cy = dependent.HeadCell.Y + dy;
                while (cx >= 0 && cx < board.Width && cy >= 0 && cy < board.Height)
                {
                    Arrow blocker = board._occupancy[cx, cy];
                    if (
                        blocker != null
                        && blocker != dependent
                        && blocker._generationIndex >= 0
                        && CanMergeForCompaction(blocker, dependent)
                    )
                    {
                        var merged = MergeArrows(blocker, dependent);
                        board.RemoveArrowForGeneration(dependent);
                        board.RemoveArrowForGeneration(blocker);
                        board.AddArrowForGeneration(merged);
                        mergeCount++;
                        changed = true;
                        yield return mergeCount;
                        break;
                    }
                    cx += dx;
                    cy += dy;
                }
            }
        }
    }

    /// <summary>
    /// Checks if two arrows can be merged: same direction, collinear,
    /// and blocker's tail is adjacent to dependent's head.
    /// </summary>
    private static bool CanMergeForCompaction(Arrow blocker, Arrow dependent)
    {
        if (blocker.HeadDirection != dependent.HeadDirection)
            return false;
        bool collinear = blocker.HeadDirection switch
        {
            Arrow.Direction.Right or Arrow.Direction.Left => blocker.HeadCell.Y
                == dependent.HeadCell.Y,
            Arrow.Direction.Up or Arrow.Direction.Down => blocker.HeadCell.X
                == dependent.HeadCell.X,
            _ => false,
        };
        if (!collinear)
            return false;

        Cell blockerTail = blocker.Cells[blocker.Cells.Count - 1];
        Cell dependentHead = dependent.HeadCell;
        int adx = Math.Abs(blockerTail.X - dependentHead.X);
        int ady = Math.Abs(blockerTail.Y - dependentHead.Y);
        return (adx == 1 && ady == 0) || (adx == 0 && ady == 1);
    }

    /// <summary>Creates a merged arrow: blocker cells followed by dependent cells.</summary>
    private static Arrow MergeArrows(Arrow blocker, Arrow dependent)
    {
        var cells = new List<Cell>(blocker.Cells.Count + dependent.Cells.Count);
        for (int i = 0; i < blocker.Cells.Count; i++)
            cells.Add(blocker.Cells[i]);
        for (int i = 0; i < dependent.Cells.Count; i++)
            cells.Add(dependent.Cells[i]);
        return new Arrow(cells);
    }
}

/// <summary>Head candidate used by <see cref="Board.InitializeForGeneration"/>.</summary>
internal struct ArrowHeadData
{
    public Cell head;
    public Cell next;
    public Arrow.Direction direction;
}
