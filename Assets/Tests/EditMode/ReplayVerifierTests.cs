using System;
using System.Collections.Generic;
using NUnit.Framework;

[TestFixture]
public class ReplayVerifierTests
{
    private const int Seed = 42;
    private const int Width = 10;
    private const int Height = 10;
    private const int MaxArrowLength = 5;

    /// <summary>
    /// Generates a board and builds a valid replay by clearing arrows in a legal order.
    /// </summary>
    private static ReplayData MakeValidReplay(
        int seed = Seed,
        int width = Width,
        int height = Height,
        int maxArrowLength = MaxArrowLength
    )
    {
        var board = new Board(width, height);
        TestBoardHelper.FillBoard(board, maxArrowLength, new Random(seed));

        var snapshot = new List<List<Cell>>();
        foreach (var arrow in board.Arrows)
            snapshot.Add(new List<Cell>(arrow.Cells));

        var events = new List<ReplayEvent>();
        int seq = 0;
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.SessionStart,
                timestamp = baseTime.ToString("O"),
            }
        );

        double t = 1.0;
        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.StartSolve,
                timestamp = baseTime.AddSeconds(t).ToString("O"),
            }
        );

        t += 0.5;
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

            var head = toClear.HeadCell;
            events.Add(
                new ReplayEvent
                {
                    seq = seq++,
                    type = ReplayEventType.Clear,
                    posX = head.X,
                    posY = head.Y,
                    timestamp = baseTime.AddSeconds(t).ToString("O"),
                }
            );
            board.RemoveArrow(toClear);
            t += 0.5;
        }

        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.EndSolve,
                timestamp = baseTime.AddSeconds(t).ToString("O"),
            }
        );

        return new ReplayData
        {
            version = 3,
            gameId = Guid.NewGuid().ToString(),
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            maxArrowLength = maxArrowLength,
            inspectionDuration = 0f,
            boardSnapshot = snapshot,
            events = events,
            finalTime = t - 1.0,
        };
    }

    [Test]
    public void Verify_ValidReplay_ReturnsValid()
    {
        var replay = MakeValidReplay();
        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.True, result.Reason);
        Assert.That(result.VerifiedTime, Is.GreaterThan(0));
    }

    [Test]
    public void Verify_ValidReplay_VerifiedTimeMatchesEvents()
    {
        var replay = MakeValidReplay();
        double expected = replay.ComputedSolveElapsed;

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.True, result.Reason);
        Assert.That(result.VerifiedTime, Is.EqualTo(expected).Within(0.001));
    }

    [Test]
    public void Verify_SnapshotMismatch_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

        // Tamper with the snapshot by removing an arrow.
        replay.boardSnapshot.RemoveAt(0);

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("mismatch"));
    }

    [Test]
    public void Verify_SnapshotArrowCellsTampered_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

        // Tamper with an arrow's cells in the snapshot.
        var firstArrow = replay.boardSnapshot[0];
        firstArrow[0] = new Cell(firstArrow[0].X + 99, firstArrow[0].Y + 99);

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("mismatch"));
    }

    [Test]
    public void Verify_ClearNonClearableArrow_ReturnsInvalid()
    {
        // Build a replay that clears arrows in wrong order.
        var board = new Board(Width, Height);
        TestBoardHelper.FillBoard(board, MaxArrowLength, new Random(Seed));

        var snapshot = new List<List<Cell>>();
        foreach (var arrow in board.Arrows)
            snapshot.Add(new List<Cell>(arrow.Cells));

        // Find an arrow that is NOT clearable.
        Arrow nonClearable = null;
        foreach (var arrow in board.Arrows)
        {
            if (!board.IsClearable(arrow))
            {
                nonClearable = arrow;
                break;
            }
        }

        // If all arrows are clearable (unlikely but possible), skip.
        if (nonClearable == null)
        {
            Assert.Inconclusive(
                "All arrows are clearable on this seed; cannot test non-clearable."
            );
            return;
        }

        var events = new List<ReplayEvent>
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
                type = ReplayEventType.StartSolve,
                timestamp = "2026-01-01T00:00:01Z",
            },
            new ReplayEvent
            {
                seq = 2,
                type = ReplayEventType.Clear,
                posX = nonClearable.HeadCell.X,
                posY = nonClearable.HeadCell.Y,
                timestamp = "2026-01-01T00:00:02Z",
            },
        };

        var replay = new ReplayData
        {
            version = 3,
            gameId = "test",
            seed = Seed,
            boardWidth = Width,
            boardHeight = Height,
            maxArrowLength = MaxArrowLength,
            boardSnapshot = snapshot,
            events = events,
            finalTime = 1.0,
        };

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("not clearable"));
    }

    [Test]
    public void Verify_TapOnEmptyCell_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

        // Replace first clear event with a tap on an empty cell.
        // Cell (-1, -1) is guaranteed out of bounds / empty.
        for (int i = 0; i < replay.events.Count; i++)
        {
            if (replay.events[i].type == ReplayEventType.Clear)
            {
                replay.events[i].posX = -1f;
                replay.events[i].posY = -1f;
                break;
            }
        }

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("No arrow"));
    }

    [Test]
    public void Verify_BoardNotEmpty_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

        // Remove the last clear event so the board isn't fully cleared.
        for (int i = replay.events.Count - 1; i >= 0; i--)
        {
            if (replay.events[i].type == ReplayEventType.Clear)
            {
                replay.events.RemoveAt(i);
                break;
            }
        }

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("not fully cleared"));
    }

    [Test]
    public void Verify_PauseGap_ExcludedFromSolveTime()
    {
        var replay = MakeValidReplay();

        // Insert a session_leave/rejoin gap of 1000 seconds just after start_solve.
        int insertIdx = -1;
        int insertSeq = -1;
        for (int i = 0; i < replay.events.Count; i++)
        {
            if (replay.events[i].type == ReplayEventType.StartSolve)
            {
                insertIdx = i + 1;
                insertSeq = replay.events[i].seq + 1;
                break;
            }
        }

        var leaveTime = DateTime
            .Parse(replay.events[insertIdx].timestamp)
            .ToUniversalTime()
            .AddSeconds(-0.1);
        var rejoinTime = leaveTime.AddSeconds(1000);

        // Shift all subsequent event timestamps forward by 1000 seconds.
        for (int i = insertIdx; i < replay.events.Count; i++)
        {
            var ts = DateTime.Parse(replay.events[i].timestamp).ToUniversalTime().AddSeconds(1000);
            replay.events[i].timestamp = ts.ToString("O");
        }

        // Insert leave/rejoin pair.
        replay.events.Insert(
            insertIdx,
            new ReplayEvent
            {
                seq = insertSeq,
                type = ReplayEventType.SessionLeave,
                timestamp = leaveTime.ToString("O"),
            }
        );
        replay.events.Insert(
            insertIdx + 1,
            new ReplayEvent
            {
                seq = insertSeq + 1,
                type = ReplayEventType.SessionRejoin,
                timestamp = rejoinTime.ToString("O"),
            }
        );

        // Fix seq numbers.
        for (int i = 0; i < replay.events.Count; i++)
            replay.events[i].seq = i;

        var resultWithPause = ReplayVerifier.Verify(replay);
        Assert.That(resultWithPause.IsValid, Is.True, resultWithPause.Reason);

        // Verified time should NOT include the 1000s gap.
        Assert.That(
            resultWithPause.VerifiedTime,
            Is.LessThan(100),
            "Verified time should exclude the pause gap."
        );
    }

    [Test]
    public void Verify_TruncatedEvents_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

        // Remove end_solve and the last few clears.
        while (replay.events.Count > 3)
            replay.events.RemoveAt(replay.events.Count - 1);

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("not fully cleared"));
    }

    [Test]
    public void Verify_NullReplay_ReturnsInvalid()
    {
        var result = ReplayVerifier.Verify(null);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("null"));
    }

    [Test]
    public void Verify_NoSnapshot_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.boardSnapshot = null;

        var result = ReplayVerifier.Verify(replay);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reason, Does.Contain("snapshot"));
    }

    [Test]
    public void Verify_MultipleSeeds_AllValid()
    {
        // Verify across several seeds to ensure robustness.
        for (int seed = 0; seed < 20; seed++)
        {
            var replay = MakeValidReplay(seed: seed);
            var result = ReplayVerifier.Verify(replay);

            Assert.That(result.IsValid, Is.True, $"Seed {seed} failed: {result.Reason}");
        }
    }

    // ── PreVerify ──────────────────────────────────────────────────────────────

    [Test]
    public void PreVerify_ValidReplay_ReturnsNull()
    {
        var replay = MakeValidReplay();
        Assert.That(ReplayVerifier.PreVerify(replay), Is.Null);
    }

    [Test]
    public void PreVerify_BoardTooSmall_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.boardWidth = 1;
        Assert.That(ReplayVerifier.PreVerify(replay), Does.Contain("dimensions"));
    }

    [Test]
    public void PreVerify_BoardTooLarge_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.boardWidth = 401;
        Assert.That(ReplayVerifier.PreVerify(replay), Does.Contain("dimensions"));
    }

    [Test]
    public void PreVerify_MaxBoardSize_ReturnsNull()
    {
        var replay = MakeValidReplay();
        replay.boardWidth = 400;
        replay.boardHeight = 400;
        Assert.That(ReplayVerifier.PreVerify(replay), Is.Null);
    }

    [Test]
    public void PreVerify_ImplausiblyFastTime_ReturnsReason()
    {
        // Build a replay with many clears but impossibly short total time
        var replay = MakeValidReplay();

        // Count clears
        int clearCount = 0;
        foreach (var evt in replay.events)
            if (evt.type == ReplayEventType.Clear)
                clearCount++;

        // Only applies when clearCount > 5
        Assert.That(clearCount, Is.GreaterThan(5), "Need >5 clears for timing check to apply");

        // Squash all timestamps into a tiny window (1ms per clear)
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double t = 0;
        for (int i = 0; i < replay.events.Count; i++)
        {
            replay.events[i].timestamp = baseTime.AddMilliseconds(t).ToString("O");
            if (replay.events[i].type == ReplayEventType.Clear)
                t += 1; // 1ms per clear
            else
                t += 1;
        }

        Assert.That(ReplayVerifier.PreVerify(replay), Does.Contain("implausibly fast"));
    }

    [Test]
    public void PreVerify_SmallClearCount_SkipsTiming()
    {
        // A replay with <= 5 clears should skip timing checks even with 0s solve time
        var board = new Board(3, 3);
        TestBoardHelper.FillBoard(board, 3, new Random(42));

        var snapshot = new List<List<Cell>>();
        foreach (var arrow in board.Arrows)
            snapshot.Add(new List<Cell>(arrow.Cells));

        // Count arrows — should be small on a 3x3 board
        int arrowCount = board.Arrows.Count;
        Assert.That(
            arrowCount,
            Is.LessThanOrEqualTo(5),
            "3x3 board should have <= 5 arrows for this test"
        );

        var events = new List<ReplayEvent>();
        int seq = 0;
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.SessionStart,
                timestamp = baseTime.ToString("O"),
            }
        );
        // All events at the same timestamp (0s solve time)
        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.StartSolve,
                timestamp = baseTime.AddSeconds(1).ToString("O"),
            }
        );
        while (board.Arrows.Count > 0)
        {
            Arrow toClear = null;
            foreach (var arrow in board.Arrows)
                if (board.IsClearable(arrow))
                {
                    toClear = arrow;
                    break;
                }
            events.Add(
                new ReplayEvent
                {
                    seq = seq++,
                    type = ReplayEventType.Clear,
                    posX = toClear.HeadCell.X,
                    posY = toClear.HeadCell.Y,
                    timestamp = baseTime.AddSeconds(1).ToString("O"),
                }
            );
            board.RemoveArrow(toClear);
        }
        events.Add(
            new ReplayEvent
            {
                seq = seq++,
                type = ReplayEventType.EndSolve,
                timestamp = baseTime.AddSeconds(1).ToString("O"),
            }
        );

        var replay = new ReplayData
        {
            version = 3,
            gameId = Guid.NewGuid().ToString(),
            seed = 42,
            boardWidth = 3,
            boardHeight = 3,
            maxArrowLength = 3,
            boardSnapshot = snapshot,
            events = events,
            finalTime = 0,
        };

        Assert.That(ReplayVerifier.PreVerify(replay), Is.Null);
    }

    [Test]
    public void PreVerify_NormalTime_ReturnsNull()
    {
        // The default MakeValidReplay uses 0.5s per clear — well above threshold
        var replay = MakeValidReplay();
        Assert.That(ReplayVerifier.PreVerify(replay), Is.Null);
    }

    [Test]
    public void PreVerify_NullReplay_ReturnsReason()
    {
        Assert.That(ReplayVerifier.PreVerify(null), Does.Contain("null"));
    }

    [Test]
    public void PreVerify_NoEvents_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.events = new List<ReplayEvent>();
        Assert.That(ReplayVerifier.PreVerify(replay), Does.Contain("no events"));
    }
}
