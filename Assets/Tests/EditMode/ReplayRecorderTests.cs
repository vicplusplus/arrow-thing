using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

[TestFixture]
public class ReplayRecorderTests
{
    // ── Seq ordering ──────────────────────────────────────────────────────────

    [Test]
    public void SeqNumbers_AreMonotonicallyIncreasing()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();
        rec.RecordStartSolve();
        rec.RecordClear(1f, 2f);
        rec.RecordReject(3f, 4f);
        rec.RecordSessionLeave();

        var events = rec.Events;
        for (int i = 1; i < events.Count; i++)
            Assert.Less(events[i - 1].seq, events[i].seq, $"seq not increasing at index {i}");
    }

    [Test]
    public void SeqNumbers_StartAtZero()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();
        Assert.AreEqual(0, rec.Events[0].seq);
    }

    // ── Event types ───────────────────────────────────────────────────────────

    [Test]
    public void RecordSessionStart_SetsCorrectType()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();
        Assert.AreEqual(ReplayEventType.SessionStart, rec.Events[0].type);
    }

    [Test]
    public void RecordSessionStart_SetsWallTime()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();
        Assert.IsNotEmpty(rec.Events[0].timestamp);
    }

    [Test]
    public void RecordSessionLeave_SetsCorrectType()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionLeave();
        Assert.AreEqual(ReplayEventType.SessionLeave, rec.Events[0].type);
    }

    [Test]
    public void RecordSessionRejoin_SetsCorrectType()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionRejoin();
        Assert.AreEqual(ReplayEventType.SessionRejoin, rec.Events[0].type);
    }

    [Test]
    public void RecordStartSolve_SetsType()
    {
        var rec = new ReplayRecorder();
        rec.RecordStartSolve();
        var evt = rec.Events[0];
        Assert.AreEqual(ReplayEventType.StartSolve, evt.type);
    }

    [Test]
    public void RecordClear_SetsTypePositionAndTimestamp()
    {
        var rec = new ReplayRecorder();
        rec.RecordClear(5f, 7f);
        var evt = rec.Events[0];
        Assert.AreEqual(ReplayEventType.Clear, evt.type);
        Assert.AreEqual(5f, evt.posX.Value, 1e-5f);
        Assert.AreEqual(7f, evt.posY.Value, 1e-5f);
        Assert.IsNotEmpty(evt.timestamp);
    }

    [Test]
    public void RecordReject_SetsTypeAndPosition()
    {
        var rec = new ReplayRecorder();
        rec.RecordReject(-3f, 4f);
        var evt = rec.Events[0];
        Assert.AreEqual(ReplayEventType.Reject, evt.type);
        Assert.AreEqual(-3f, evt.posX.Value, 1e-5f);
        Assert.AreEqual(4f, evt.posY.Value, 1e-5f);
    }

    [Test]
    public void RecordEndSolve_SetsTypeAndTimestamp()
    {
        var rec = new ReplayRecorder();
        rec.RecordEndSolve();
        var evt = rec.Events[0];
        Assert.AreEqual(ReplayEventType.EndSolve, evt.type);
        Assert.IsNotEmpty(evt.timestamp);
    }

    // ── start_solve + clear same-timestamp scenario ───────────────────────────

    [Test]
    public void StartSolveAndClear_OrderedBySeq()
    {
        var rec = new ReplayRecorder();
        rec.RecordStartSolve();
        rec.RecordClear(2f, 3f);

        Assert.AreEqual(2, rec.Events.Count);
        Assert.AreEqual(0, rec.Events[0].seq);
        Assert.AreEqual(1, rec.Events[1].seq);
        Assert.AreEqual(ReplayEventType.StartSolve, rec.Events[0].type);
        Assert.AreEqual(ReplayEventType.Clear, rec.Events[1].type);
    }

    // ── ToReplayData ──────────────────────────────────────────────────────────

    [Test]
    public void ToReplayData_IncludesAllEvents()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();
        rec.RecordClear(0f, 0f);

        var data = rec.ToReplayData("game-1", 42, 10, 10, 20, 15f);
        Assert.AreEqual(2, data.events.Count);
        Assert.AreEqual("game-1", data.gameId);
        Assert.AreEqual(42, data.seed);
        Assert.AreEqual(10, data.boardWidth);
        Assert.AreEqual(10, data.boardHeight);
        Assert.AreEqual(20, data.maxArrowLength);
        Assert.AreEqual(15f, data.inspectionDuration, 1e-5f);
        Assert.AreEqual(-1.0, data.finalTime, 1e-9); // in-progress
    }

    [Test]
    public void ToReplayData_FinalTime_SetWhenProvided()
    {
        var rec = new ReplayRecorder();
        rec.RecordClear(0f, 0f);
        var data = rec.ToReplayData("g", 0, 5, 5, 10, 15f, finalTime: 9.99);
        Assert.AreEqual(9.99, data.finalTime, 1e-9);
    }

    [Test]
    public void ToReplayData_EventsListIsACopy()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();
        var data = rec.ToReplayData("g", 0, 5, 5, 10, 15f);

        // Adding more events after snapshot does not affect the snapshot
        rec.RecordClear(0f, 0f);
        Assert.AreEqual(1, data.events.Count);
        Assert.AreEqual(2, rec.Events.Count);
    }

    // ── boardSnapshot ─────────────────────────────────────────────────────────

    [Test]
    public void ToReplayData_WithSnapshot_SnapshotIsPreserved()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();

        var snapshot = new List<List<Cell>>
        {
            new List<Cell> { new Cell(0, 0), new Cell(0, 1) },
            new List<Cell> { new Cell(2, 3), new Cell(3, 3), new Cell(4, 3) },
        };

        var data = rec.ToReplayData("g", 1, 5, 5, 10, 15f, boardSnapshot: snapshot);

        Assert.IsNotNull(data.boardSnapshot);
        var arrows = data.GetSnapshotArrows();
        Assert.AreEqual(2, arrows.Count);
        Assert.AreEqual(new Cell(0, 0), arrows[0][0]);
        Assert.AreEqual(new Cell(0, 1), arrows[0][1]);
        Assert.AreEqual(3, arrows[1].Count);
    }

    [Test]
    public void ToReplayData_NewRecorder_SetsCurrentReplayVersion()
    {
        var rec = new ReplayRecorder();
        var snapshot = new List<List<Cell>>
        {
            new List<Cell> { new Cell(0, 0), new Cell(1, 0) },
        };
        var data = rec.ToReplayData("g", 0, 5, 5, 10, 15f, boardSnapshot: snapshot);
        Assert.AreEqual(5, data.version);
    }

    [Test]
    public void ToReplayData_WithoutSnapshot_SnapshotIsNull()
    {
        var rec = new ReplayRecorder();
        rec.RecordSessionStart();
        var data = rec.ToReplayData("g", 0, 5, 5, 10, 15f);
        Assert.IsNull(data.boardSnapshot);
    }

    [Test]
    public void ResumeWithSnapshot_CanRestoreAndReplayClearsOnBoard()
    {
        // Initial board: 3 arrows
        var initialSnapshot = new List<List<Cell>>
        {
            new List<Cell> { new Cell(0, 0), new Cell(0, 1), new Cell(0, 2) },
            new List<Cell> { new Cell(3, 3), new Cell(2, 3) },
            new List<Cell> { new Cell(5, 5), new Cell(5, 4) },
        };

        // Restore all arrows from snapshot
        var board = new Board(6, 6);
        foreach (var arrowCells in initialSnapshot)
            board.AddArrow(new Arrow(arrowCells));

        Assert.AreEqual(3, board.Arrows.Count);

        // Simulate replaying a clear event (arrow at 5,5 was cleared)
        Arrow toClear = board.GetArrowAt(new Cell(5, 5));
        Assert.IsNotNull(toClear);
        Assert.IsTrue(board.IsClearable(toClear));
        board.RemoveArrow(toClear);

        Assert.AreEqual(2, board.Arrows.Count);

        // Verify remaining arrows match
        var restoredCells0 = board.Arrows[0].Cells.ToList();
        Assert.AreEqual(new Cell(0, 0), restoredCells0[0]);
        Assert.AreEqual(new Cell(0, 2), restoredCells0[2]);
    }

    // ── Resume from prior events ──────────────────────────────────────────────

    [Test]
    public void Resume_ContinuesSeqFromNextSeq()
    {
        var prior = new List<ReplayEvent>
        {
            new ReplayEvent
            {
                seq = 0,
                type = ReplayEventType.SessionStart,
                timestamp = "2026-01-01T00:00:00Z",
            },
            new ReplayEvent
            {
                seq = 1,
                type = ReplayEventType.Clear,
                timestamp = "2026-01-01T00:00:01Z",
            },
        };

        var rec = new ReplayRecorder(prior, nextSeq: 2);
        rec.RecordSessionRejoin();

        // Orphan rejoin injects a session_leave before the rejoin
        Assert.AreEqual(4, rec.Events.Count);
        Assert.AreEqual(2, rec.Events[2].seq);
        Assert.AreEqual(ReplayEventType.SessionLeave, rec.Events[2].type);
        Assert.AreEqual(3, rec.Events[3].seq);
        Assert.AreEqual(ReplayEventType.SessionRejoin, rec.Events[3].type);
    }

    [Test]
    public void Resume_PriorEventsArePreserved()
    {
        var prior = new List<ReplayEvent>
        {
            new ReplayEvent
            {
                seq = 0,
                type = ReplayEventType.SessionStart,
                timestamp = "2026-01-01T00:00:00Z",
            },
            new ReplayEvent
            {
                seq = 1,
                type = ReplayEventType.Clear,
                posX = 1f,
                posY = 2f,
                timestamp = "2026-01-01T00:00:01Z",
            },
        };

        var rec = new ReplayRecorder(prior, nextSeq: 2);

        Assert.AreEqual(0, rec.Events[0].seq);
        Assert.AreEqual(ReplayEventType.SessionStart, rec.Events[0].type);
        Assert.AreEqual(1, rec.Events[1].seq);
        Assert.AreEqual(1f, rec.Events[1].posX.Value, 1e-5f);
    }

    [Test]
    public void Resume_PriorListNotMutated()
    {
        var prior = new List<ReplayEvent>
        {
            new ReplayEvent
            {
                seq = 0,
                type = ReplayEventType.SessionStart,
                timestamp = "2026-01-01T00:00:00Z",
            },
        };

        var rec = new ReplayRecorder(prior, nextSeq: 1);
        rec.RecordClear(0f, 0f);

        // The original list should not have been modified
        Assert.AreEqual(1, prior.Count);
    }

    // ── ComputedSolveElapsed ──────────────────────────────────────────────────

    [Test]
    public void ComputedSolveElapsed_SimpleSolve_ReturnsCorrectDuration()
    {
        var data = new ReplayData
        {
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.StartSolve,
                    timestamp = "2026-01-01T00:00:15.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 1f,
                    posY = 1f,
                    timestamp = "2026-01-01T00:00:16.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 2f,
                    posY = 2f,
                    timestamp = "2026-01-01T00:00:20.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.EndSolve,
                    timestamp = "2026-01-01T00:00:20.000Z",
                },
            },
        };
        Assert.AreEqual(5.0, data.ComputedSolveElapsed, 0.001);
    }

    [Test]
    public void ComputedSolveElapsed_WithLeaveAndRejoin_ExcludesPausedTime()
    {
        var data = new ReplayData
        {
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.StartSolve,
                    timestamp = "2026-01-01T00:00:10.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 1f,
                    posY = 1f,
                    timestamp = "2026-01-01T00:00:13.000Z",
                },
                // Leave at 3s into solve, rejoin 100s later
                new ReplayEvent
                {
                    type = ReplayEventType.SessionLeave,
                    timestamp = "2026-01-01T00:00:13.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.SessionRejoin,
                    timestamp = "2026-01-01T00:01:53.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 2f,
                    posY = 2f,
                    timestamp = "2026-01-01T00:01:55.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.EndSolve,
                    timestamp = "2026-01-01T00:01:55.000Z",
                },
            },
        };
        // 3s active + 2s active = 5s total (100s paused time excluded)
        Assert.AreEqual(5.0, data.ComputedSolveElapsed, 0.001);
    }

    [Test]
    public void ComputedSolveElapsed_UnterminatedSession_IncludesTimeToLastEvent()
    {
        // Autosave or force-quit: no session_leave or end_solve.
        // Should include time up to the last recorded event so resume
        // doesn't lose the current session's solve time.
        var data = new ReplayData
        {
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.StartSolve,
                    timestamp = "2026-01-01T00:00:10.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 1f,
                    posY = 1f,
                    timestamp = "2026-01-01T00:00:17.500Z",
                },
            },
        };
        Assert.AreEqual(7.5, data.ComputedSolveElapsed, 0.001);
    }

    [Test]
    public void ComputedSolveElapsed_WithSessionLeave_IncludesCompletedInterval()
    {
        // Proper save always ends with session_leave, closing the interval
        var data = new ReplayData
        {
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.StartSolve,
                    timestamp = "2026-01-01T00:00:10.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 1f,
                    posY = 1f,
                    timestamp = "2026-01-01T00:00:17.500Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.SessionLeave,
                    timestamp = "2026-01-01T00:00:17.500Z",
                },
            },
        };
        Assert.AreEqual(7.5, data.ComputedSolveElapsed, 0.001);
    }

    [Test]
    public void ComputedSolveElapsed_MultiSession_UnterminatedTail()
    {
        // First session saved gracefully, second session autosaved (no session_leave).
        // Should include time from both sessions.
        var data = new ReplayData
        {
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    type = ReplayEventType.StartSolve,
                    timestamp = "2026-01-01T00:00:00.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 1f,
                    posY = 1f,
                    timestamp = "2026-01-01T00:00:03.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.SessionLeave,
                    timestamp = "2026-01-01T00:00:03.000Z",
                },
                // Gap: 100 seconds paused
                new ReplayEvent
                {
                    type = ReplayEventType.SessionRejoin,
                    timestamp = "2026-01-01T00:01:43.000Z",
                },
                new ReplayEvent
                {
                    type = ReplayEventType.Clear,
                    posX = 2f,
                    posY = 2f,
                    timestamp = "2026-01-01T00:01:45.000Z",
                },
                // No session_leave — autosave or force-quit
            },
        };
        // 3s (first session) + 2s (second session unterminated tail) = 5s
        Assert.AreEqual(5.0, data.ComputedSolveElapsed, 0.001);
    }

    [Test]
    public void ComputedSolveElapsed_NoStartSolve_ReturnsZero()
    {
        var data = new ReplayData
        {
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00.000Z",
                },
            },
        };
        Assert.AreEqual(0.0, data.ComputedSolveElapsed, 0.001);
    }
}
