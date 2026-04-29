using System;
using System.Collections.Generic;

namespace ArrowThing.Server.Games;

/// <summary>
/// Verifies a completed endless-mode replay by replaying its events through
/// <see cref="EndlessRun"/> — the same domain class the live game uses, no
/// parallel simulator. Each tap event is asserted against the run's
/// classification of the cell at that sim time; the topout event's sim time
/// is asserted against the run's. Final claimed totals are compared to the
/// run's own <see cref="EndlessRun.ClearCount"/> /
/// <see cref="EndlessRun.LongestCombo"/> / <see cref="EndlessRun.RunDurationSeconds"/>.
///
/// <para>Determinism contract: identical {seed, paletteCount, tuningsVersion,
/// boardWidth, boardHeight} + identical event sequence must produce
/// identical run state on client and server. <see cref="EndlessRun"/> uses
/// <see cref="System.Math"/> primitives that match Mono's <c>Mathf.*</c>
/// at single-precision, but parity tests are recommended before relying on
/// it for prod submissions.</para>
/// </summary>
public static class EndlessReplayVerifier
{
    /// <summary>
    /// Cheap static checks before the full replay walk. Validates board
    /// dimensions, presence of events, claimed-totals plausibility.
    /// Returns null if all checks pass; returns a reason string on failure.
    /// </summary>
    public static string? PreVerify(ReplayData replay)
    {
        if (replay == null)
            return "Replay is null.";
        if (replay.mode != GameMode.Endless)
            return "Replay is not an endless run.";
        if (replay.boardWidth < 5 || replay.boardHeight < 5)
            return "Endless board too small.";
        if (replay.boardWidth > 80 || replay.boardHeight > 80)
            return "Endless board too large.";
        if (replay.events == null || replay.events.Count == 0)
            return "Replay has no events.";
        if (replay.clears < 0)
            return "Negative clear count claimed.";
        if (replay.longestCombo < 0)
            return "Negative longest combo claimed.";
        if (replay.durationSeconds is float duration && duration < 0)
            return "Negative run duration claimed.";
        return null;
    }

    public static EndlessVerificationResult Verify(ReplayData replay, int paletteCount)
    {
        try
        {
            return VerifyInternal(replay, paletteCount);
        }
        catch (Exception ex)
        {
            return EndlessVerificationResult.Invalid("Verification failed: " + ex.Message);
        }
    }

    private static EndlessVerificationResult VerifyInternal(ReplayData replay, int paletteCount)
    {
        if (replay.tuningsVersion is not int tuningVersion)
            return EndlessVerificationResult.Invalid("Replay missing tuningsVersion.");

        var board = new Board(replay.boardWidth, replay.boardHeight);
        var tuning = EndlessTuning.ForVersion(tuningVersion);
        var run = new EndlessRun(board, replay.seed, paletteCount, tuning);
        // Verifier doesn't subscribe to spawn events, but Begin must still be
        // called so the initial-push pending arrows match the live game's
        // post-Begin state.
        run.Begin();

        float lastSimTime = 0f;
        bool sawTopout = false;

        foreach (ReplayEvent evt in replay.events)
        {
            // Advance the run's sim-time clock to this event's moment, if
            // the event carries a simTime (tap + topout events do; session
            // markers don't). Push ticks + commits whose threshold falls
            // in this interval fire here.
            if (evt.simTime is float t)
            {
                if (t < lastSimTime - 0.001f)
                    return EndlessVerificationResult.Invalid(
                        $"Event {evt.seq} simTime regressed ({t} < {lastSimTime})."
                    );
                run.Advance(t - lastSimTime);
                lastSimTime = t;
            }

            switch (evt.type)
            {
                case ReplayEventType.Clear:
                    if (evt.posX is null || evt.posY is null)
                        return EndlessVerificationResult.Invalid(
                            $"Clear event {evt.seq} missing position."
                        );
                    if (run.HandleTap(evt.posX.Value, evt.posY.Value) != TapResult.Cleared)
                        return EndlessVerificationResult.Invalid(
                            $"Clear event {evt.seq}: cell was not clearable in simulation."
                        );
                    break;

                case ReplayEventType.Reject:
                    if (evt.posX is null || evt.posY is null)
                        return EndlessVerificationResult.Invalid(
                            $"Reject event {evt.seq} missing position."
                        );
                    if (run.HandleTap(evt.posX.Value, evt.posY.Value) != TapResult.Blocked)
                        return EndlessVerificationResult.Invalid(
                            $"Reject event {evt.seq}: cell wasn't blocked in simulation."
                        );
                    break;

                case ReplayEventType.Miss:
                    if (evt.posX is null || evt.posY is null)
                        return EndlessVerificationResult.Invalid(
                            $"Miss event {evt.seq} missing position."
                        );
                    if (run.HandleTap(evt.posX.Value, evt.posY.Value) != TapResult.Missed)
                        return EndlessVerificationResult.Invalid(
                            $"Miss event {evt.seq}: cell wasn't empty in simulation."
                        );
                    break;

                case ReplayEventType.Topout:
                    sawTopout = true;
                    // After the Advance(t - lastSimTime) above, the run
                    // should have detected the topout (push tick fired
                    // with shortfall>0). If it's still active, the player
                    // claimed a topout the simulator wouldn't agree on.
                    if (run.IsActive)
                        return EndlessVerificationResult.Invalid(
                            $"Topout event {evt.seq}: run still active in simulation."
                        );
                    break;

                // session_start / session_leave / session_rejoin / start_solve /
                // end_solve don't apply to endless. They wouldn't appear on a
                // well-formed payload but we tolerate their presence (skip).
            }
        }

        if (!sawTopout)
            return EndlessVerificationResult.Invalid("Replay has no topout event.");

        // Compare claimed totals to what the run actually produced.
        if (replay.clears is int claimedClears && run.ClearCount != claimedClears)
            return EndlessVerificationResult.Invalid(
                $"Clears mismatch: claimed {claimedClears}, simulation produced {run.ClearCount}."
            );
        if (replay.longestCombo is int claimedCombo && run.LongestCombo != claimedCombo)
            return EndlessVerificationResult.Invalid(
                $"LongestCombo mismatch: claimed {claimedCombo}, simulation produced {run.LongestCombo}."
            );
        // Float comparison with a small tolerance — sim time is an
        // accumulated float, so exact equality is fragile.
        const double durationTolerance = 0.01;
        if (
            replay.durationSeconds is float claimedDuration
            && Math.Abs(run.RunDurationSeconds - claimedDuration) > durationTolerance
        )
            return EndlessVerificationResult.Invalid(
                $"Duration mismatch: claimed {claimedDuration}, simulation produced {run.RunDurationSeconds}."
            );

        return EndlessVerificationResult.Valid(
            run.ClearCount,
            run.LongestCombo,
            run.RunDurationSeconds
        );
    }
}
