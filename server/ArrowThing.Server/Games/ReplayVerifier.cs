using System;
using System.Collections.Generic;

namespace ArrowThing.Server.Games;

/// <summary>
/// Verifies a completed replay by regenerating the board from seed,
/// comparing it against the snapshot, and simulating all clear events.
/// </summary>
public static class ReplayVerifier
{
    private const double MinSecondsPerArrow = 0.08;
    private const int TimingCheckMinClearCount = 5;
    private const int MinBoardDimension = 2;
    private const int MaxBoardDimension = 400;

    /// <summary>
    /// Cheap static checks that run before expensive board regeneration.
    /// Validates board dimensions and solve timing plausibility.
    /// Returns null if all checks pass; returns a reason string on failure.
    /// </summary>
    public static string? PreVerify(ReplayData replay)
    {
        if (replay == null)
            return "Replay is null.";

        // Board size validation
        if (
            replay.boardWidth < MinBoardDimension
            || replay.boardHeight < MinBoardDimension
            || replay.boardWidth > MaxBoardDimension
            || replay.boardHeight > MaxBoardDimension
        )
            return "Board dimensions out of range.";

        if (replay.events == null || replay.events.Count == 0)
            return "Replay has no events.";

        // Count successful clears only
        int clearCount = 0;
        for (int i = 0; i < replay.events.Count; i++)
        {
            if (replay.events[i].type == ReplayEventType.Clear)
                clearCount++;
        }

        // Timing checks skipped for small boards (clearCount <= 5)
        if (clearCount > TimingCheckMinClearCount)
        {
            double solveTime = replay.ComputedSolveElapsed;
            double minTime = clearCount * MinSecondsPerArrow;
            if (solveTime < minTime)
                return "Solve time implausibly fast.";
        }

        return null;
    }

    public static VerificationResult Verify(ReplayData replay)
    {
        try
        {
            return VerifyInternal(replay);
        }
        catch (Exception)
        {
            return VerificationResult.Invalid("Verification failed.");
        }
    }

    private static VerificationResult VerifyInternal(ReplayData replay)
    {
        if (replay == null)
            return VerificationResult.Invalid("Replay is null.");

        if (replay.boardSnapshot == null || replay.boardSnapshot.Count == 0)
            return VerificationResult.Invalid("Replay has no board snapshot.");

        if (replay.events == null || replay.events.Count == 0)
            return VerificationResult.Invalid("Replay has no events.");

        // Step 1: Regenerate board from seed and compare to snapshot.
        var board = new Board(replay.boardWidth, replay.boardHeight);
        var gen = BoardGeneration.FillBoardIncremental(board, replay.maxArrowLength, replay.seed);
        while (gen.MoveNext()) { }

        var snapshotError = CompareSnapshot(board, replay.boardSnapshot);
        if (snapshotError != null)
            return VerificationResult.Invalid(snapshotError);

        // Step 2: Walk clear events and validate each.
        foreach (var evt in replay.events)
        {
            if (evt.type != ReplayEventType.Clear)
                continue;

            if (evt.posX == null || evt.posY == null)
                return VerificationResult.Invalid("Clear event missing position.");

            var cell = new Cell((int)Math.Round(evt.posX.Value), (int)Math.Round(evt.posY.Value));

            var arrow = board.GetArrowAt(cell);
            if (arrow == null)
                return VerificationResult.Invalid($"No arrow at cell ({cell.X}, {cell.Y}).");

            if (!board.IsClearable(arrow))
                return VerificationResult.Invalid(
                    $"Arrow at ({cell.X}, {cell.Y}) is not clearable."
                );

            board.RemoveArrow(arrow);
        }

        // Step 3: Board must be fully cleared.
        if (board.Arrows.Count > 0)
            return VerificationResult.Invalid(
                $"Board not fully cleared ({board.Arrows.Count} arrows remaining)."
            );

        // Step 4: Compute verified solve time from event timestamps.
        double verifiedTime = replay.ComputedSolveElapsed;

        return VerificationResult.Valid(verifiedTime);
    }

    private static string? CompareSnapshot(Board board, List<List<Cell>> snapshot)
    {
        if (board.Arrows.Count != snapshot.Count)
            return $"Snapshot mismatch: expected {board.Arrows.Count} arrows, snapshot has {snapshot.Count}.";

        // Build a set of snapshot arrows for comparison.
        // Each arrow is identified by its ordered cell list.
        var snapshotSet = new HashSet<string>(snapshot.Count);
        foreach (var arrowCells in snapshot)
        {
            snapshotSet.Add(CellsToKey(arrowCells));
        }

        foreach (var arrow in board.Arrows)
        {
            var key = CellsToKey(arrow.Cells);
            if (!snapshotSet.Contains(key))
                return "Snapshot mismatch: generated board contains an arrow not in the snapshot.";
        }

        return null;
    }

    private static string CellsToKey(IReadOnlyList<Cell> cells)
    {
        // Use a simple string key for set comparison.
        var parts = new string[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            parts[i] = cells[i].X + "," + cells[i].Y;
        return string.Join(";", parts);
    }

    private static string CellsToKey(List<Cell> cells)
    {
        var parts = new string[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            parts[i] = cells[i].X + "," + cells[i].Y;
        return string.Join(";", parts);
    }
}
