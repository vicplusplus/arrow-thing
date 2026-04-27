using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// Sanity coverage for <see cref="EndlessRun"/> determinism + lifecycle.
/// The same {seed, board size, tuning, palette count, sequence of
/// (Advance, HandleTap) calls} must produce the same {ClearCount,
/// LongestCombo, RunDurationSeconds, meter trajectory}. The future
/// replay verifier instantiates this class directly to verify; tests
/// here lock in that the run is deterministic in isolation, before
/// the verifier even exists.
/// </summary>
[TestFixture]
public class EndlessRunTests
{
    private static EndlessRun MakeRun(int seed = 42, int size = 10, int paletteCount = 6)
    {
        var board = new Board(size, size);
        return new EndlessRun(board, seed, paletteCount, EndlessTuning.V1);
    }

    [Test]
    public void NewRun_HasOneCombo_FiresFirstPushImmediately()
    {
        var run = MakeRun();
        // Constructor fires first push tick → meter has the seeded combo
        // PLUS the new one pushed at the back. So 1 active combo + 1 queued.
        Assert.That(run.Meter.Count, Is.EqualTo(1));
        Assert.That(run.IsActive, Is.True);
        Assert.That(run.ClearCount, Is.EqualTo(0));
        Assert.That(run.SimTime, Is.EqualTo(0f));
        // First push spawned pending arrows.
        Assert.That(run.PendingArrows.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Determinism_SameInputs_ProduceSameStats()
    {
        var runA = MakeRun(seed: 1234);
        var runB = MakeRun(seed: 1234);

        // Drive both runs with the same script: advance to a few sim times,
        // tap a deterministic cell pattern, advance more.
        var script = new (float dt, int? tapX, int? tapY)[]
        {
            (1.0f, null, null),
            (0.5f, 0, 0),
            (1.0f, 1, 1),
            (1.0f, null, null),
            (0.5f, 2, 2),
            (2.0f, null, null),
        };

        foreach (var step in script)
        {
            runA.Advance(step.dt);
            runB.Advance(step.dt);
            if (step.tapX is int x && step.tapY is int y)
            {
                var ra = runA.HandleTap(x, y);
                var rb = runB.HandleTap(x, y);
                Assert.That(rb, Is.EqualTo(ra), "Tap kind must match across runs");
            }
        }

        Assert.That(runB.ClearCount, Is.EqualTo(runA.ClearCount));
        Assert.That(runB.LongestCombo, Is.EqualTo(runA.LongestCombo));
        Assert.That(runB.RunDurationSeconds, Is.EqualTo(runA.RunDurationSeconds));
        Assert.That(runB.IsActive, Is.EqualTo(runA.IsActive));
        Assert.That(runB.Meter.Count, Is.EqualTo(runA.Meter.Count));
    }

    [Test]
    public void HandleTap_OnEmptyCell_ReturnsMissed()
    {
        var run = MakeRun();
        // First push spawned pending arrows; pending arrows are NOT in
        // Board.Arrows yet, so a tap on a cell without a real arrow
        // returns Missed. Pick a cell that should be clear: opposite
        // corner from typical spawn placements.
        var emptyCell = FindEmptyCell(run);
        Assume.That(
            emptyCell,
            Is.Not.Null,
            "expected at least one empty cell on small fresh board"
        );
        var (cx, cy) = emptyCell.Value;
        Assert.That(run.HandleTap(cx, cy), Is.EqualTo(EndlessTapKind.Missed));
        Assert.That(run.ClearCount, Is.EqualTo(0));
    }

    [Test]
    public void HandleTap_OutOfBounds_ReturnsMissed()
    {
        var run = MakeRun();
        Assert.That(run.HandleTap(-1, 0), Is.EqualTo(EndlessTapKind.Missed));
        Assert.That(run.HandleTap(0, 999), Is.EqualTo(EndlessTapKind.Missed));
    }

    [Test]
    public void Advance_NegativeOrZeroDelta_IsNoOp()
    {
        var run = MakeRun();
        float beforeSim = run.SimTime;
        int beforeClears = run.ClearCount;
        run.Advance(0f);
        run.Advance(-1f);
        Assert.That(run.SimTime, Is.EqualTo(beforeSim));
        Assert.That(run.ClearCount, Is.EqualTo(beforeClears));
    }

    [Test]
    public void RunDurationSeconds_LocksAtTopout()
    {
        // Drive the run forward until topout. Easy way: advance many
        // seconds without tapping. The garbage queue piles up and the
        // next push after a non-zero shortfall ends the run.
        var run = MakeRun(size: 5);
        bool topped = false;
        run.ToppedOut += _ => topped = true;

        for (int i = 0; i < 200 && !topped; i++)
            run.Advance(0.5f);

        Assume.That(topped, Is.True, "expected topout within 100 sim seconds on a 5×5");
        float duration = run.RunDurationSeconds;
        Assert.That(run.IsActive, Is.False);

        // Further advances don't move duration after topout.
        run.Advance(5f);
        Assert.That(run.RunDurationSeconds, Is.EqualTo(duration));
    }

    private static (int, int)? FindEmptyCell(EndlessRun run)
    {
        var occupied = new HashSet<(int, int)>();
        foreach (var p in run.PendingArrows)
        foreach (var c in p.Arrow.Cells)
            occupied.Add((c.X, c.Y));
        for (int y = 0; y < run.Board.Height; y++)
        for (int x = 0; x < run.Board.Width; x++)
            if (!occupied.Contains((x, y)) && run.Board.GetArrowAt(new Cell(x, y)) == null)
                return (x, y);
        return null;
    }
}
