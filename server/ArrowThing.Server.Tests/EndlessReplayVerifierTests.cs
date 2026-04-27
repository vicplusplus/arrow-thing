using ArrowThing.Server.Games;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Pure verifier-level tests for <see cref="EndlessReplayVerifier"/>. No HTTP,
/// no DB — each test builds a <see cref="ReplayData"/> payload by driving a
/// real <see cref="EndlessRun"/> and recording events the same way the live
/// client does (via <see cref="ReplayRecorder"/>), then calls
/// <c>Verify(replay, paletteCount: 6)</c> and asserts on the returned result.
/// Mirrors <see cref="ReplayVerifierTests"/> for classic.
/// </summary>
public class EndlessReplayVerifierTests
{
    private const int PaletteCount = 6;
    private const int DefaultSeed = 42;
    private const int DefaultWidth = 10;
    private const int DefaultHeight = 10;

    /// <summary>
    /// Drives a fresh <see cref="EndlessRun"/> until it tops out by passing
    /// time without ever tapping. This is the simplest kind of valid run:
    /// ClearCount=0, LongestCombo=0, the player just lets the meter overflow.
    /// Returns the assembled <see cref="ReplayData"/> ready for verification.
    /// </summary>
    private static ReplayData BuildPassiveTopoutReplay(
        int seed = DefaultSeed,
        int width = DefaultWidth,
        int height = DefaultHeight,
        int tuningsVersion = 1
    )
    {
        var board = new Board(width, height);
        var tuning = EndlessTuning.ForVersion(tuningsVersion);
        var run = new EndlessRun(board, seed, PaletteCount, tuning);

        var recorder = new ReplayRecorder();
        recorder.RecordSessionStart();

        // Advance in small steps until the run topouts. Cap the loop in
        // case of tuning regressions so a runaway test fails fast.
        const float dt = 0.1f;
        const float maxSimSeconds = 600f;
        float simAcc = 0f;
        while (run.IsActive && simAcc < maxSimSeconds)
        {
            run.Advance(dt);
            simAcc += dt;
        }
        if (run.IsActive)
            throw new InvalidOperationException(
                "Test setup error: passive run did not top out within "
                    + $"{maxSimSeconds}s — tuning may have changed."
            );

        recorder.RecordTopout(run.RunDurationSeconds);

        return new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Endless,
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            tuningsVersion = tuningsVersion,
            clears = run.ClearCount,
            longestCombo = run.LongestCombo,
            durationSeconds = run.RunDurationSeconds,
            events = new List<ReplayEvent>(recorder.Events),
            finalTime = -1.0,
        };
    }

    /// <summary>
    /// Drives an <see cref="EndlessRun"/> with a few real clears mixed in
    /// before letting it top out. Produces a richer replay that exercises
    /// the Clear path, the simTime advancement between events, and the
    /// final ClearCount > 0 / LongestCombo > 0 assertions on the verifier.
    /// </summary>
    private static ReplayData BuildActiveTopoutReplay(
        int seed = DefaultSeed,
        int width = DefaultWidth,
        int height = DefaultHeight,
        int maxClearAttempts = 5
    )
    {
        var board = new Board(width, height);
        var tuning = EndlessTuning.ForVersion(1);
        var run = new EndlessRun(board, seed, PaletteCount, tuning);

        var recorder = new ReplayRecorder();
        recorder.RecordSessionStart();

        const float dt = 0.1f;
        const float maxSimSeconds = 600f;
        float simAcc = 0f;
        int clearsDone = 0;
        while (run.IsActive && simAcc < maxSimSeconds)
        {
            run.Advance(dt);
            simAcc += dt;

            // Try to clear an arrow whenever there's a clearable one and
            // we haven't hit our cap. Tap on the head cell which the
            // run's HandleTap rounds back to the cell we want.
            if (clearsDone < maxClearAttempts && run.IsActive)
            {
                Arrow? clearable = null;
                foreach (var arrow in run.Board.Arrows)
                {
                    if (run.Board.IsClearable(arrow))
                    {
                        clearable = arrow;
                        break;
                    }
                }
                if (clearable != null)
                {
                    var head = clearable.HeadCell;
                    var result = run.HandleTap(head.X, head.Y);
                    if (result == TapResult.Cleared)
                    {
                        recorder.RecordClear(head.X, head.Y, run.SimTime);
                        clearsDone++;
                    }
                }
            }
        }
        if (run.IsActive)
            throw new InvalidOperationException(
                "Test setup error: active run did not top out within " + $"{maxSimSeconds}s."
            );

        recorder.RecordTopout(run.RunDurationSeconds);

        return new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Endless,
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            tuningsVersion = 1,
            clears = run.ClearCount,
            longestCombo = run.LongestCombo,
            durationSeconds = run.RunDurationSeconds,
            events = new List<ReplayEvent>(recorder.Events),
            finalTime = -1.0,
        };
    }

    // ---- Happy path -------------------------------------------------------

    [Fact]
    public void Verify_PassiveTopoutReplay_IsValid()
    {
        var replay = BuildPassiveTopoutReplay();

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(replay.clears, result.VerifiedClears);
        Assert.Equal(replay.longestCombo, result.VerifiedLongestCombo);
        Assert.Equal(
            (double)replay.durationSeconds!.Value,
            result.VerifiedDurationSeconds,
            precision: 2
        );
    }

    [Fact]
    public void Verify_ActiveTopoutReplay_IsValid()
    {
        var replay = BuildActiveTopoutReplay();

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.True(result.IsValid, result.Reason);
        // VerifiedClears should match what we recorded.
        Assert.Equal(replay.clears, result.VerifiedClears);
        Assert.True(result.VerifiedClears > 0);
        Assert.Equal(replay.longestCombo, result.VerifiedLongestCombo);
    }

    // ---- Event-level mismatches -------------------------------------------

    [Fact]
    public void Verify_MissingTopoutEvent_Rejected()
    {
        var replay = BuildPassiveTopoutReplay();
        // Drop the topout event — verifier should reject.
        replay.events.RemoveAll(e => e.type == ReplayEventType.Topout);

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("topout", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_ClearEventOnEmptyCell_Rejected()
    {
        var replay = BuildPassiveTopoutReplay();
        // Insert a Clear event at a coordinate that is empty in the
        // simulation — passive replay never spawned at the corner the
        // first push tick avoids, but to be safe pick (0,0) which the
        // run may or may not occupy. Use a cell that is definitely
        // out-of-bounds so HandleTap returns Missed → not Cleared.
        // Insert before the topout event.
        var topoutIdx = replay.events.FindIndex(e => e.type == ReplayEventType.Topout);
        var emptyTap = new ReplayEvent
        {
            seq = 999,
            type = ReplayEventType.Clear,
            posX = -10,
            posY = -10,
            simTime = replay.events[topoutIdx].simTime!.Value - 0.001f,
            timestamp = "2026-01-01T00:00:00Z",
        };
        replay.events.Insert(topoutIdx, emptyTap);

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("clear", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectEventOnClearableArrow_Rejected()
    {
        // Drive a run forward enough that at least one arrow exists and is
        // clearable at the moment we record the bogus Reject event.
        var board = new Board(DefaultWidth, DefaultHeight);
        var tuning = EndlessTuning.ForVersion(1);
        var run = new EndlessRun(board, DefaultSeed, PaletteCount, tuning);

        var recorder = new ReplayRecorder();
        recorder.RecordSessionStart();

        // Advance until first commit produces real arrows. The first push
        // fires immediately in the constructor, the first commit lands at
        // ~0.6 * push_interval. Advance a couple of seconds to be safe.
        const float dt = 0.1f;
        for (int i = 0; i < 30 && run.IsActive; i++)
            run.Advance(dt);

        // Find a clearable arrow.
        Arrow? clearable = null;
        foreach (var arrow in run.Board.Arrows)
        {
            if (run.Board.IsClearable(arrow))
            {
                clearable = arrow;
                break;
            }
        }
        Assert.NotNull(clearable);

        // Record a Reject event on the head of a clearable arrow — the
        // verifier's HandleTap will return Cleared, not Blocked, so it
        // should reject.
        var head = clearable!.HeadCell;
        recorder.RecordReject(head.X, head.Y, run.SimTime);
        // Then drive to topout.
        while (run.IsActive)
            run.Advance(dt);
        recorder.RecordTopout(run.RunDurationSeconds);

        var replay = new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Endless,
            seed = DefaultSeed,
            boardWidth = DefaultWidth,
            boardHeight = DefaultHeight,
            tuningsVersion = 1,
            clears = 0,
            longestCombo = 0,
            durationSeconds = run.RunDurationSeconds,
            events = new List<ReplayEvent>(recorder.Events),
        };

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("reject", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissEventOnArrowCell_Rejected()
    {
        // Same setup as the Reject test but record a Miss event on a
        // clearable cell — verifier expects Missed but simulation says
        // Cleared. (HandleTap returns Cleared on a clearable arrow, not
        // Missed, so this is a valid mismatch path.)
        var board = new Board(DefaultWidth, DefaultHeight);
        var tuning = EndlessTuning.ForVersion(1);
        var run = new EndlessRun(board, DefaultSeed, PaletteCount, tuning);

        var recorder = new ReplayRecorder();
        recorder.RecordSessionStart();

        const float dt = 0.1f;
        for (int i = 0; i < 30 && run.IsActive; i++)
            run.Advance(dt);

        Arrow? clearable = null;
        foreach (var arrow in run.Board.Arrows)
        {
            if (run.Board.IsClearable(arrow))
            {
                clearable = arrow;
                break;
            }
        }
        Assert.NotNull(clearable);

        var head = clearable!.HeadCell;
        recorder.RecordMiss(head.X, head.Y, run.SimTime);
        while (run.IsActive)
            run.Advance(dt);
        recorder.RecordTopout(run.RunDurationSeconds);

        var replay = new ReplayData
        {
            version = 7,
            gameId = Guid.NewGuid().ToString(),
            mode = GameMode.Endless,
            seed = DefaultSeed,
            boardWidth = DefaultWidth,
            boardHeight = DefaultHeight,
            tuningsVersion = 1,
            clears = 0,
            longestCombo = 0,
            durationSeconds = run.RunDurationSeconds,
            events = new List<ReplayEvent>(recorder.Events),
        };

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("miss", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_TopoutClaimedWhileRunActive_Rejected()
    {
        // Build a topout-by-time replay, then trim the topout's simTime
        // back to a moment when the simulation was still active. The
        // verifier advances to that simTime and observes IsActive=true.
        var replay = BuildPassiveTopoutReplay();
        var topout = replay.events.Find(e => e.type == ReplayEventType.Topout)!;
        // Move the topout to t=0.5s (well before the actual topout time).
        topout.simTime = 0.5f;

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("active", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_SimTimeRegression_Rejected()
    {
        // Build an active replay and corrupt one event so its simTime is
        // earlier than the previous one.
        var replay = BuildActiveTopoutReplay();
        var taps = replay.events.FindAll(e =>
            e.type == ReplayEventType.Clear && e.simTime is float
        );
        Assert.True(taps.Count >= 2, "Need at least two clears for the regression test.");
        // Force a regression: second clear simTime < first clear simTime.
        taps[1].simTime = taps[0].simTime!.Value - 1.0f;

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("regress", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Claimed-totals mismatches ----------------------------------------

    [Fact]
    public void Verify_ClaimedClearsMismatch_Rejected()
    {
        var replay = BuildPassiveTopoutReplay();
        replay.clears = (replay.clears ?? 0) + 50;

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("clears mismatch", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_ClaimedDurationMismatch_Rejected()
    {
        var replay = BuildPassiveTopoutReplay();
        // Push duration off by far more than the 0.01s tolerance.
        replay.durationSeconds = (replay.durationSeconds ?? 0f) + 5f;

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("duration", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissingTuningsVersion_Rejected()
    {
        var replay = BuildPassiveTopoutReplay();
        replay.tuningsVersion = null;

        var result = EndlessReplayVerifier.Verify(replay, PaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("tuning", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- PreVerify gates --------------------------------------------------

    [Fact]
    public void PreVerify_OversizedBoard_Rejected()
    {
        var replay = new ReplayData
        {
            version = 7,
            mode = GameMode.Endless,
            boardWidth = 81,
            boardHeight = 10,
            tuningsVersion = 1,
            clears = 0,
            longestCombo = 0,
            durationSeconds = 1f,
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    seq = 0,
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00Z",
                },
            },
        };

        var reason = EndlessReplayVerifier.PreVerify(replay);

        Assert.NotNull(reason);
        Assert.Contains("large", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreVerify_UndersizedBoard_Rejected()
    {
        var replay = new ReplayData
        {
            version = 7,
            mode = GameMode.Endless,
            boardWidth = 4,
            boardHeight = 10,
            tuningsVersion = 1,
            clears = 0,
            longestCombo = 0,
            durationSeconds = 1f,
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    seq = 0,
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00Z",
                },
            },
        };

        var reason = EndlessReplayVerifier.PreVerify(replay);

        Assert.NotNull(reason);
        Assert.Contains("small", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreVerify_EmptyEvents_Rejected()
    {
        var replay = new ReplayData
        {
            version = 7,
            mode = GameMode.Endless,
            boardWidth = 10,
            boardHeight = 10,
            tuningsVersion = 1,
            clears = 0,
            longestCombo = 0,
            durationSeconds = 1f,
            events = new List<ReplayEvent>(),
        };

        var reason = EndlessReplayVerifier.PreVerify(replay);

        Assert.NotNull(reason);
        Assert.Contains("event", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreVerify_NegativeClears_Rejected()
    {
        var replay = new ReplayData
        {
            version = 7,
            mode = GameMode.Endless,
            boardWidth = 10,
            boardHeight = 10,
            tuningsVersion = 1,
            clears = -1,
            longestCombo = 0,
            durationSeconds = 1f,
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    seq = 0,
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00Z",
                },
            },
        };

        var reason = EndlessReplayVerifier.PreVerify(replay);

        Assert.NotNull(reason);
        Assert.Contains("negative", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreVerify_WrongMode_Rejected()
    {
        var replay = new ReplayData
        {
            version = 7,
            mode = GameMode.Classic,
            boardWidth = 10,
            boardHeight = 10,
            tuningsVersion = 1,
            clears = 0,
            longestCombo = 0,
            durationSeconds = 1f,
            events = new List<ReplayEvent>
            {
                new ReplayEvent
                {
                    seq = 0,
                    type = ReplayEventType.SessionStart,
                    timestamp = "2026-01-01T00:00:00Z",
                },
            },
        };

        var reason = EndlessReplayVerifier.PreVerify(replay);

        Assert.NotNull(reason);
        Assert.Contains("endless", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreVerify_ValidShape_ReturnsNull()
    {
        var replay = BuildPassiveTopoutReplay();
        var reason = EndlessReplayVerifier.PreVerify(replay);
        Assert.Null(reason);
    }
}
