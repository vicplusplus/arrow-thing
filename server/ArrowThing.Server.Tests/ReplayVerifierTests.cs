using ArrowThing.Server.Games;

namespace ArrowThing.Server.Tests;

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
        TestBoardHelper.FillBoard(board, maxArrowLength, seed);

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

    [Fact]
    public void Verify_ValidReplay_ReturnsValid()
    {
        var replay = MakeValidReplay();
        var result = ReplayVerifier.Verify(replay);

        Assert.True(result.IsValid, result.Reason);
        Assert.True(result.VerifiedTime > 0);
    }

    [Fact]
    public void Verify_ValidReplay_VerifiedTimeMatchesEvents()
    {
        var replay = MakeValidReplay();
        double expected = replay.ComputedSolveElapsed;

        var result = ReplayVerifier.Verify(replay);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(expected, result.VerifiedTime, 3);
    }

    [Fact]
    public void Verify_SnapshotMismatch_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.boardSnapshot.RemoveAt(0);

        var result = ReplayVerifier.Verify(replay);

        Assert.False(result.IsValid);
        Assert.Contains("mismatch", result.Reason);
    }

    [Fact]
    public void Verify_SnapshotArrowCellsTampered_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        var firstArrow = replay.boardSnapshot[0];
        firstArrow[0] = new Cell(firstArrow[0].X + 99, firstArrow[0].Y + 99);

        var result = ReplayVerifier.Verify(replay);

        Assert.False(result.IsValid);
        Assert.Contains("mismatch", result.Reason);
    }

    [Fact]
    public void Verify_ClearNonClearableArrow_ReturnsInvalid()
    {
        var board = new Board(Width, Height);
        TestBoardHelper.FillBoard(board, MaxArrowLength, Seed);

        var snapshot = new List<List<Cell>>();
        foreach (var arrow in board.Arrows)
            snapshot.Add(new List<Cell>(arrow.Cells));

        Arrow nonClearable = null;
        foreach (var arrow in board.Arrows)
        {
            if (!board.IsClearable(arrow))
            {
                nonClearable = arrow;
                break;
            }
        }

        // Skip if all arrows are clearable on this seed
        if (nonClearable == null)
            return;

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

        Assert.False(result.IsValid);
        Assert.Contains("not clearable", result.Reason);
    }

    [Fact]
    public void Verify_TapOnEmptyCell_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

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

        Assert.False(result.IsValid);
        Assert.Contains("No arrow", result.Reason);
    }

    [Fact]
    public void Verify_BoardNotEmpty_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

        for (int i = replay.events.Count - 1; i >= 0; i--)
        {
            if (replay.events[i].type == ReplayEventType.Clear)
            {
                replay.events.RemoveAt(i);
                break;
            }
        }

        var result = ReplayVerifier.Verify(replay);

        Assert.False(result.IsValid);
        Assert.Contains("not fully cleared", result.Reason);
    }

    [Fact]
    public void Verify_PauseGap_ExcludedFromSolveTime()
    {
        var replay = MakeValidReplay();

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

        for (int i = insertIdx; i < replay.events.Count; i++)
        {
            var ts = DateTime.Parse(replay.events[i].timestamp).ToUniversalTime().AddSeconds(1000);
            replay.events[i].timestamp = ts.ToString("O");
        }

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

        for (int i = 0; i < replay.events.Count; i++)
            replay.events[i].seq = i;

        var resultWithPause = ReplayVerifier.Verify(replay);
        Assert.True(resultWithPause.IsValid, resultWithPause.Reason);
        Assert.True(
            resultWithPause.VerifiedTime < 100,
            "Verified time should exclude the pause gap."
        );
    }

    [Fact]
    public void Verify_TruncatedEvents_ReturnsInvalid()
    {
        var replay = MakeValidReplay();

        while (replay.events.Count > 3)
            replay.events.RemoveAt(replay.events.Count - 1);

        var result = ReplayVerifier.Verify(replay);

        Assert.False(result.IsValid);
        Assert.Contains("not fully cleared", result.Reason);
    }

    [Fact]
    public void Verify_NullReplay_ReturnsInvalid()
    {
        var result = ReplayVerifier.Verify(null);

        Assert.False(result.IsValid);
        Assert.Contains("null", result.Reason);
    }

    [Fact]
    public void Verify_NoSnapshot_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.boardSnapshot = null;

        var result = ReplayVerifier.Verify(replay);

        Assert.False(result.IsValid);
        Assert.Contains("snapshot", result.Reason);
    }

    [Fact]
    public void Verify_MultipleSeeds_AllValid()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var replay = MakeValidReplay(seed: seed);
            var result = ReplayVerifier.Verify(replay);

            Assert.True(result.IsValid, $"Seed {seed} failed: {result.Reason}");
        }
    }

    // ── PreVerify ──────────────────────────────────────────────────────────────

    [Fact]
    public void PreVerify_ValidReplay_ReturnsNull()
    {
        var replay = MakeValidReplay();
        Assert.Null(ReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_BoardTooSmall_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.boardWidth = 1;
        Assert.Contains("dimensions", ReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_BoardTooLarge_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.boardWidth = 401;
        Assert.Contains("dimensions", ReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_MaxBoardSize_ReturnsNull()
    {
        var replay = MakeValidReplay();
        replay.boardWidth = 400;
        replay.boardHeight = 400;
        Assert.Null(ReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_ImplausiblyFastTime_ReturnsReason()
    {
        var replay = MakeValidReplay();

        int clearCount = 0;
        foreach (var evt in replay.events)
            if (evt.type == ReplayEventType.Clear)
                clearCount++;

        Assert.True(clearCount > 5, "Need >5 clears for timing check to apply");

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double t = 0;
        for (int i = 0; i < replay.events.Count; i++)
        {
            replay.events[i].timestamp = baseTime.AddMilliseconds(t).ToString("O");
            if (replay.events[i].type == ReplayEventType.Clear)
                t += 1;
            else
                t += 1;
        }

        Assert.Contains("implausibly fast", ReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_SmallClearCount_SkipsTiming()
    {
        var board = new Board(3, 3);
        TestBoardHelper.FillBoard(board, 3, 42);

        var snapshot = new List<List<Cell>>();
        foreach (var arrow in board.Arrows)
            snapshot.Add(new List<Cell>(arrow.Cells));

        int arrowCount = board.Arrows.Count;
        Assert.True(arrowCount <= 5, "3x3 board should have <= 5 arrows for this test");

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

        Assert.Null(ReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_NormalTime_ReturnsNull()
    {
        var replay = MakeValidReplay();
        Assert.Null(ReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_NullReplay_ReturnsReason()
    {
        Assert.Contains("null", ReplayVerifier.PreVerify(null));
    }

    [Fact]
    public void PreVerify_NoEvents_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.events = new List<ReplayEvent>();
        Assert.Contains("no events", ReplayVerifier.PreVerify(replay));
    }
}
