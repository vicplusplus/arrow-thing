using ArrowThing.Server.Games;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Coverage for <see cref="EndlessReplayVerifier"/>. Mirrors the structure
/// of <see cref="ReplayVerifierTests"/> for classic, but the simulator is
/// time-driven instead of clear-driven so the golden replay is built by
/// running an <see cref="EndlessRun"/> forward in the same way the live
/// game does and capturing the resulting events + final stats.
///
/// The tests intentionally cover the rejection paths the verifier exists
/// to enforce: tampered totals, fabricated topouts, regressed timing,
/// out-of-bounds positions, missing topout, and unknown tuning version.
/// Each rejection should hit the verifier's specific "Invalid" branch and
/// surface a Reason that makes the cause obvious.
/// </summary>
public class EndlessReplayVerifierTests
{
    private const int DefaultSeed = 42;
    private const int DefaultBoardWidth = 10;
    private const int DefaultBoardHeight = 10;
    private const int DefaultPaletteCount = 6;
    private const int CurrentTuningVersion = EndlessTuning.CurrentVersion;

    /// <summary>
    /// Drives an <see cref="EndlessRun"/> forward in fixed-step ticks until
    /// it tops out (or a hard step cap is hit, which we treat as a test bug).
    /// Captures every observable transition into a <see cref="ReplayData"/>
    /// the verifier should accept exactly. The player never taps — the
    /// run is shape-checked by the unstoppable garbage queue alone, which
    /// makes the resulting golden replay deterministic and minimal.
    /// </summary>
    private static ReplayData MakeValidReplay(
        int seed = DefaultSeed,
        int width = DefaultBoardWidth,
        int height = DefaultBoardHeight,
        int paletteCount = DefaultPaletteCount,
        int tuningVersion = CurrentTuningVersion
    )
    {
        var board = new Board(width, height);
        var tuning = EndlessTuning.ForVersion(tuningVersion);
        var run = new EndlessRun(board, seed, paletteCount, tuning);

        // Step the run forward. 0.25 s per tick is comfortably below any
        // push interval at default tuning, so we won't accidentally skip
        // multiple pushes inside a single Advance — the verifier handles
        // that case but tests are clearer when the steps are uniform.
        const float stepSeconds = 0.25f;
        const int hardStepCap = 4000; // ~1000 sim seconds; way more than needed.

        float simTime = 0f;
        int steps = 0;
        while (run.IsActive && steps++ < hardStepCap)
        {
            simTime += stepSeconds;
            run.Advance(stepSeconds);
        }

        if (run.IsActive)
        {
            throw new InvalidOperationException(
                $"Run never topped out within {hardStepCap} steps; tighten the test "
                    + "or pick a board / seed combo that tops out faster."
            );
        }

        // The actual sim-time stamped on the topout event must come from
        // the run, not the wall-clock loop above — Advance can fire topout
        // mid-step and freeze the duration there.
        float topoutSimTime = run.RunDurationSeconds;

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
                type = ReplayEventType.Topout,
                simTime = topoutSimTime,
                timestamp = "2026-01-01T00:00:00Z",
            },
        };

        return new ReplayData
        {
            version = 7,
            mode = GameMode.Endless,
            gameId = Guid.NewGuid().ToString(),
            seed = seed,
            boardWidth = width,
            boardHeight = height,
            maxArrowLength = tuning.EndlessMaxArrowLength,
            tuningsVersion = tuningVersion,
            clears = run.ClearCount,
            longestCombo = run.LongestCombo,
            durationSeconds = run.RunDurationSeconds,
            events = events,
        };
    }

    // ── Pre-verify (cheap shape gate) ──────────────────────────────────

    [Fact]
    public void PreVerify_ValidReplay_ReturnsNull()
    {
        var replay = MakeValidReplay();
        Assert.Null(EndlessReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_NotEndless_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.mode = GameMode.Classic;
        Assert.Contains("not an endless run", EndlessReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_TooSmall_ReturnsReason()
    {
        var replay = MakeValidReplay(width: 4, height: 4);
        Assert.Contains("too small", EndlessReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_TooLarge_ReturnsReason()
    {
        // Build a real replay at a runnable size, then tamper the dims so
        // the shape gate fires without spending a slow run on an 80x80.
        var replay = MakeValidReplay();
        replay.boardWidth = 100;
        Assert.Contains("too large", EndlessReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_NoEvents_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.events = new List<ReplayEvent>();
        Assert.Contains("no events", EndlessReplayVerifier.PreVerify(replay));
    }

    [Fact]
    public void PreVerify_NegativeClears_ReturnsReason()
    {
        var replay = MakeValidReplay();
        replay.clears = -1;
        Assert.Contains("Negative clear count", EndlessReplayVerifier.PreVerify(replay));
    }

    // ── Full verify: happy path ────────────────────────────────────────

    [Fact]
    public void Verify_ValidReplay_ReturnsValid()
    {
        var replay = MakeValidReplay();
        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(replay.clears!.Value, result.VerifiedClears);
        Assert.Equal(replay.longestCombo!.Value, result.VerifiedLongestCombo);
        Assert.Equal(replay.durationSeconds!.Value, result.VerifiedDurationSeconds, precision: 2);
    }

    [Fact]
    public void Verify_DeterministicAcrossInstances()
    {
        // Same inputs → same verified outputs. The verifier is the only
        // judge of a submitted score; if it isn't byte-for-byte
        // deterministic for the same replay, we'd flag legitimate runs.
        var a = MakeValidReplay(seed: 1234);
        var b = MakeValidReplay(seed: 1234);

        var ra = EndlessReplayVerifier.Verify(a, DefaultPaletteCount);
        var rb = EndlessReplayVerifier.Verify(b, DefaultPaletteCount);

        Assert.True(ra.IsValid && rb.IsValid);
        Assert.Equal(ra.VerifiedClears, rb.VerifiedClears);
        Assert.Equal(ra.VerifiedLongestCombo, rb.VerifiedLongestCombo);
        Assert.Equal(ra.VerifiedDurationSeconds, rb.VerifiedDurationSeconds, precision: 6);
    }

    // ── Tampered totals ────────────────────────────────────────────────

    [Fact]
    public void Verify_ClearsInflated_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.clears = (replay.clears ?? 0) + 50;

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("Clears mismatch", result.Reason);
    }

    [Fact]
    public void Verify_LongestComboInflated_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.longestCombo = (replay.longestCombo ?? 0) + 25;

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("LongestCombo mismatch", result.Reason);
    }

    [Fact]
    public void Verify_DurationFar_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.durationSeconds = (replay.durationSeconds ?? 0f) + 60f;

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("Duration mismatch", result.Reason);
    }

    // ── Tampered timing ────────────────────────────────────────────────

    [Fact]
    public void Verify_RegressedSimTime_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        // Inject a clear event with a simTime that goes backwards. Use a
        // synthetic position — the verifier should reject on the time
        // regression before ever evaluating the position.
        replay.events.Insert(
            1,
            new ReplayEvent
            {
                seq = 100,
                type = ReplayEventType.Clear,
                posX = 0,
                posY = 0,
                simTime = -1f,
                timestamp = "2026-01-01T00:00:00Z",
            }
        );

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("simTime regressed", result.Reason);
    }

    // ── Topout claims ──────────────────────────────────────────────────

    [Fact]
    public void Verify_NoTopoutEvent_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.events.RemoveAll(e => e.type == ReplayEventType.Topout);

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("no topout event", result.Reason);
    }

    [Fact]
    public void Verify_TopoutClaimedTooEarly_ReturnsInvalid()
    {
        // Pull the topout's simTime back to 0.5 s — the run can't have
        // ended that early on a 10×10 with default tuning. The verifier
        // should advance to 0.5 s, find IsActive==true, and reject.
        var replay = MakeValidReplay();
        var topout = replay.events.Last(e => e.type == ReplayEventType.Topout);
        topout.simTime = 0.5f;

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("run still active", result.Reason);
    }

    // ── Tampered tap positions ─────────────────────────────────────────

    [Fact]
    public void Verify_ClearMissingPosition_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        // Insert a clear event at simTime=0.1 (before topout) with no
        // position. The verifier should reject on the missing-position
        // branch, not blow up trying to read posX.
        replay.events.Insert(
            1,
            new ReplayEvent
            {
                seq = 100,
                type = ReplayEventType.Clear,
                simTime = 0.1f,
                timestamp = "2026-01-01T00:00:00Z",
            }
        );

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("missing position", result.Reason);
    }

    [Fact]
    public void Verify_FakeClearOnEmptyCell_ReturnsInvalid()
    {
        // Claim a clear at a cell that's almost certainly empty on the
        // freshly-spawned board (the run hasn't progressed yet). The
        // verifier should reject because HandleTap returns Missed, not
        // Cleared.
        var replay = MakeValidReplay();
        replay.events.Insert(
            1,
            new ReplayEvent
            {
                seq = 100,
                type = ReplayEventType.Clear,
                posX = -5,
                posY = -5,
                simTime = 0.1f,
                timestamp = "2026-01-01T00:00:00Z",
            }
        );

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("not clearable", result.Reason);
    }

    // ── Tuning version ─────────────────────────────────────────────────

    [Fact]
    public void Verify_MissingTuningsVersion_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.tuningsVersion = null;

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        Assert.Contains("missing tuningsVersion", result.Reason);
    }

    [Fact]
    public void Verify_UnknownTuningsVersion_ReturnsInvalid()
    {
        var replay = MakeValidReplay();
        replay.tuningsVersion = 9999;

        var result = EndlessReplayVerifier.Verify(replay, DefaultPaletteCount);

        Assert.False(result.IsValid);
        // Wrapped by the catch-all in Verify(); reason starts with
        // "Verification failed:" and carries the inner message.
        Assert.Contains("Verification failed", result.Reason);
        Assert.Contains("9999", result.Reason);
    }
}
