using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// Sanity coverage for <see cref="ClassicRun"/>: HandleTap classification,
/// timer transitions, recorder emission, board mutation on Cleared,
/// BoardCleared event on the last arrow, RegisterViewTap parity, and
/// post-completion no-ops.
///
/// The future replay verifier instantiates this class directly, walks
/// recorded events, and compares run state to claimed stats. These tests
/// lock in deterministic behavior on the per-tap level before the verifier
/// even exists.
/// </summary>
[TestFixture]
public class ClassicRunTests
{
    /// <summary>
    /// Builds a 5×5 board with two horizontally-aligned right-pointing arrows
    /// on row 0:
    ///   Arrow B: cells [(4,0), (3,0)] — head (4,0) faces Right, ray to
    ///   boundary is empty → clearable from the start.
    ///   Arrow A: cells [(2,0), (1,0)] — head (2,0) faces Right, ray
    ///   contains (3,0) which is B's body → blocked until B is cleared.
    /// Solving order: B then A.
    /// </summary>
    private static (Board board, Arrow a, Arrow b) MakeTwoArrowBoard()
    {
        var board = new Board(5, 5);
        var b = new Arrow(new[] { new Cell(4, 0), new Cell(3, 0) });
        var a = new Arrow(new[] { new Cell(2, 0), new Cell(1, 0) });
        board.AddArrow(b);
        board.AddArrow(a);
        return (board, a, b);
    }

    [Test]
    public void NewRun_HasZeroClearsAndNotCompleted()
    {
        var (board, _, _) = MakeTwoArrowBoard();
        var run = new ClassicRun(board);
        Assert.That(run.ClearedCount, Is.EqualTo(0));
        Assert.That(run.IsCompleted, Is.False);
        Assert.That(run.Board.Arrows.Count, Is.EqualTo(2));
    }

    [Test]
    public void HandleTap_OutOfBounds_ReturnsMissed_NoRecord()
    {
        var (board, _, _) = MakeTwoArrowBoard();
        var recorder = new ReplayRecorder();
        var run = new ClassicRun(board, recorder);
        Assert.That(run.HandleTap(-1f, 0f, 0.0), Is.EqualTo(TapResult.Missed));
        Assert.That(run.HandleTap(0f, 999f, 0.0), Is.EqualTo(TapResult.Missed));
        // Out-of-bounds is a defensive no-op: nothing recorded, no timer
        // transition. (Live path InputHandler filters these out before
        // they ever reach HandleTap.)
        Assert.That(recorder.Events, Is.Empty);
    }

    [Test]
    public void HandleTap_OnEmptyCell_ReturnsMissed_RecordsMiss_NoTimerTransition()
    {
        var (board, _, _) = MakeTwoArrowBoard();
        var recorder = new ReplayRecorder();
        var timer = new GameTimer(15.0);
        timer.Start(0.0);
        var run = new ClassicRun(board, recorder, timer);

        // (4,4) is empty (only row 0 has arrows on this board).
        Assert.That(run.HandleTap(4f, 4f, 0.5), Is.EqualTo(TapResult.Missed));

        var miss = FindOne(recorder, ReplayEventType.Miss);
        Assert.That(miss.posX, Is.EqualTo(4f));
        Assert.That(miss.posY, Is.EqualTo(4f));
        Assert.That(timer.IsSolving, Is.False, "Missed tap must not end inspection");
        Assert.That(
            CountEvents(recorder, ReplayEventType.StartSolve),
            Is.Zero,
            "Missed must not emit start_solve"
        );
    }

    [Test]
    public void HandleTap_OnBlockedArrow_ReturnsBlocked_RecordsReject_TransitionsTimer()
    {
        var (board, a, _) = MakeTwoArrowBoard();
        var recorder = new ReplayRecorder();
        var timer = new GameTimer(15.0);
        timer.Start(0.0);
        var run = new ClassicRun(board, recorder, timer);

        // Tap arrow A (head (2,0)) — blocked because B's body sits in A's ray.
        Assert.That(run.HandleTap(a.HeadCell.X, a.HeadCell.Y, 1.0), Is.EqualTo(TapResult.Blocked));

        var reject = FindOne(recorder, ReplayEventType.Reject);
        Assert.That(reject.posX, Is.EqualTo(2f));
        Assert.That(timer.IsSolving, Is.True, "Blocked arrow tap must end inspection");
        Assert.That(
            CountEvents(recorder, ReplayEventType.StartSolve),
            Is.EqualTo(1),
            "Blocked tap should emit a single start_solve"
        );
        Assert.That(run.Board.Arrows.Count, Is.EqualTo(2), "Blocked tap must not mutate board");
    }

    [Test]
    public void HandleTap_ClearableArrow_FirstClear_MutatesBoardAndStartsSolve()
    {
        var (board, _, b) = MakeTwoArrowBoard();
        var recorder = new ReplayRecorder();
        var timer = new GameTimer(15.0);
        timer.Start(0.0);
        var run = new ClassicRun(board, recorder, timer);

        // The first cleared tap is just TapResult.Cleared — "first" is
        // derivable from run.ClearedCount == 1, not part of the result vocabulary.
        TapResult result = run.HandleTap(b.HeadCell.X, b.HeadCell.Y, 0.75);
        Assert.That(result, Is.EqualTo(TapResult.Cleared));
        Assert.That(run.ClearedCount, Is.EqualTo(1));
        Assert.That(run.IsCompleted, Is.False, "more arrows remain");
        Assert.That(run.Board.Arrows.Count, Is.EqualTo(1));
        Assert.That(run.Board.GetArrowAt(new Cell(4, 0)), Is.Null);
        Assert.That(timer.IsSolving, Is.True);
        Assert.That(CountEvents(recorder, ReplayEventType.Clear), Is.EqualTo(1));
        Assert.That(CountEvents(recorder, ReplayEventType.StartSolve), Is.EqualTo(1));
    }

    [Test]
    public void HandleTap_LastArrow_FiresBoardClearedAndFinishesTimer()
    {
        var (board, a, b) = MakeTwoArrowBoard();
        var recorder = new ReplayRecorder();
        var timer = new GameTimer(15.0);
        timer.Start(0.0);
        var run = new ClassicRun(board, recorder, timer);

        bool boardClearedFired = false;
        run.BoardCleared += () => boardClearedFired = true;

        // Clear B first to unblock A.
        run.HandleTap(b.HeadCell.X, b.HeadCell.Y, 0.5);
        Assert.That(boardClearedFired, Is.False, "BoardCleared must not fire mid-run");
        Assert.That(run.IsCompleted, Is.False);

        // Clear A: now the last arrow → BoardCleared fires, run.IsCompleted
        // flips, timer transitions to Finished with the wall time we passed.
        TapResult result = run.HandleTap(a.HeadCell.X, a.HeadCell.Y, 2.5);
        Assert.That(result, Is.EqualTo(TapResult.Cleared));
        Assert.That(boardClearedFired, Is.True);
        Assert.That(run.IsCompleted, Is.True);
        Assert.That(run.Board.Arrows.Count, Is.EqualTo(0));
        Assert.That(timer.CurrentPhase, Is.EqualTo(GameTimer.Phase.Finished));
        // SolveElapsed = wallTime at finish - wallTime at first non-missed tap.
        // First non-missed tap was at 0.5; finish at 2.5 → elapsed 2.0.
        Assert.That(timer.SolveElapsed, Is.EqualTo(2.0).Within(1e-9));
    }

    [Test]
    public void HandleTap_AfterCompletion_IsNoOp_AndReturnsMissed()
    {
        var (board, a, b) = MakeTwoArrowBoard();
        var recorder = new ReplayRecorder();
        var run = new ClassicRun(board, recorder);
        run.HandleTap(b.HeadCell.X, b.HeadCell.Y, 0.0);
        run.HandleTap(a.HeadCell.X, a.HeadCell.Y, 1.0);
        Assume.That(run.IsCompleted, Is.True);

        int eventsBefore = recorder.Events.Count;
        Assert.That(run.HandleTap(0f, 0f, 5.0), Is.EqualTo(TapResult.Missed));
        Assert.That(
            recorder.Events.Count,
            Is.EqualTo(eventsBefore),
            "Post-completion taps must not append events"
        );
    }

    [Test]
    public void HandleTap_RoundsFractionalGridPos_ToNearestCell()
    {
        var (board, _, b) = MakeTwoArrowBoard();
        var run = new ClassicRun(board);
        // (3.6, 0.4) rounds to (4, 0) → arrow B's head, clearable.
        Assert.That(run.HandleTap(3.6f, 0.4f, 0.0), Is.EqualTo(TapResult.Cleared));
    }

    [Test]
    public void RegisterViewTap_MatchesHandleTap_TimerAndRecorderState()
    {
        // Drive two parallel runs through the same script: one via HandleTap
        // (verifier-style; mutates the board itself), one via RegisterViewTap
        // (live-style; we mutate the board manually to mirror what BoardView
        // would have done before calling RegisterViewTap). Final state must
        // agree on cleared count, timer state, and emitted events.
        var (boardA, aA, bA) = MakeTwoArrowBoard();
        var recA = new ReplayRecorder();
        var timerA = new GameTimer(15.0);
        timerA.Start(0.0);
        var runA = new ClassicRun(boardA, recA, timerA);

        var (boardB, aB, bB) = MakeTwoArrowBoard();
        var recB = new ReplayRecorder();
        var timerB = new GameTimer(15.0);
        timerB.Start(0.0);
        var runB = new ClassicRun(boardB, recB, timerB);

        // Run A: HandleTap path.
        runA.HandleTap(0f, 4f, 0.25); // empty cell → Missed
        runA.HandleTap(aA.HeadCell.X, aA.HeadCell.Y, 0.5); // blocked → Blocked
        runA.HandleTap(bA.HeadCell.X, bA.HeadCell.Y, 0.75); // clear B
        runA.HandleTap(aA.HeadCell.X, aA.HeadCell.Y, 1.0); // clear A (last)

        // Run B: RegisterViewTap path. Caller mutates board for cleared
        // results (mirroring BoardView.TryClearArrow's role on the live path).
        runB.RegisterViewTap(TapResult.Missed, 0f, 4f, 0.25);
        runB.RegisterViewTap(TapResult.Blocked, aB.HeadCell.X, aB.HeadCell.Y, 0.5);
        boardB.RemoveArrow(bB);
        runB.RegisterViewTap(TapResult.Cleared, bB.HeadCell.X, bB.HeadCell.Y, 0.75);
        boardB.RemoveArrow(aB);
        runB.RegisterViewTap(TapResult.Cleared, aB.HeadCell.X, aB.HeadCell.Y, 1.0);

        Assert.That(runB.ClearedCount, Is.EqualTo(runA.ClearedCount));
        Assert.That(runB.IsCompleted, Is.EqualTo(runA.IsCompleted));
        Assert.That(timerB.CurrentPhase, Is.EqualTo(timerA.CurrentPhase));
        Assert.That(timerB.SolveElapsed, Is.EqualTo(timerA.SolveElapsed).Within(1e-9));
        Assert.That(recB.Events.Count, Is.EqualTo(recA.Events.Count));
        for (int i = 0; i < recA.Events.Count; i++)
            Assert.That(recB.Events[i].type, Is.EqualTo(recA.Events[i].type), $"event[{i}] type");
    }

    [Test]
    public void Resume_AlreadyClearedSeed_AffectsClearedCount()
    {
        var (board, _, b) = MakeTwoArrowBoard();
        // Pretend B was cleared in a prior session: remove from board and
        // construct the run with alreadyCleared=1 so the next clear is the
        // 2nd (and final) of the run.
        board.RemoveArrow(b);
        var run = new ClassicRun(board, recorder: null, timer: null, alreadyCleared: 1);
        Assert.That(run.ClearedCount, Is.EqualTo(1));

        // Tap the remaining arrow A — its ray to the boundary is now empty.
        TapResult result = run.HandleTap(2f, 0f, 1.0);
        Assert.That(result, Is.EqualTo(TapResult.Cleared));
        Assert.That(run.ClearedCount, Is.EqualTo(2));
        Assert.That(run.IsCompleted, Is.True);
    }

    [Test]
    public void HandleTap_NullRecorderAndTimer_DoesNotThrow()
    {
        var (board, _, b) = MakeTwoArrowBoard();
        var run = new ClassicRun(board, recorder: null, timer: null);
        Assert.That(run.HandleTap(b.HeadCell.X, b.HeadCell.Y, 0.0), Is.EqualTo(TapResult.Cleared));
        Assert.That(run.ClearedCount, Is.EqualTo(1));
    }

    // ---- Helpers ----------------------------------------------------------

    private static int CountEvents(ReplayRecorder recorder, string type)
    {
        int n = 0;
        foreach (ReplayEvent e in recorder.Events)
            if (e.type == type)
                n++;
        return n;
    }

    private static ReplayEvent FindOne(ReplayRecorder recorder, string type)
    {
        ReplayEvent found = null;
        foreach (ReplayEvent e in recorder.Events)
        {
            if (e.type == type)
            {
                Assert.That(found, Is.Null, $"expected exactly one {type} event");
                found = e;
            }
        }
        Assert.That(found, Is.Not.Null, $"expected a {type} event");
        return found;
    }
}
