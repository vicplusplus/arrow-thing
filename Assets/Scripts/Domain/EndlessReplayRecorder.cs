using System;
using System.Collections.Generic;

/// <summary>
/// Captures the player input log for an endless run. Pure C#, no Unity
/// dependency — sim-time timestamps come from the caller (driven by
/// <c>EndlessModeController._simTime</c> on the client; advanced
/// step-by-step on the server replay simulator).
///
/// What's recorded:
///   - Every player tap inside the board (cell + sim time + outcome
///     kind). Outcome includes Missed (empty cell) and Blocked (un-
///     clearable arrow) because both affect <c>_lastClearTime</c> /
///     combo-streak state on replay.
///   - The final topout event (sim time only).
///
/// What's NOT recorded:
///   - Push ticks, pending spawns, commits — all derivable from
///     {seed, board size, tap log} on the verifier given the same
///     simulator code.
///   - Real wall-clock timestamps.
/// </summary>
public sealed class EndlessReplayRecorder
{
    private readonly List<EndlessReplayEvent> _events = new();
    private bool _toppedOut;

    public IReadOnlyList<EndlessReplayEvent> Events => _events;
    public bool ToppedOut => _toppedOut;

    /// <summary>
    /// Records a player tap. <paramref name="kind"/> conveys what the tap
    /// resolved to so the verifier can fail-fast on a mismatch (e.g. claim
    /// of Cleared on a cell that was empty at that sim time).
    /// </summary>
    public void RecordTap(float simTime, int cellX, int cellY, EndlessTapKind kind)
    {
        _events.Add(
            new EndlessReplayEvent
            {
                type = EndlessReplayEventType.Tap,
                simTime = simTime,
                cellX = cellX,
                cellY = cellY,
                tapKind = kind,
            }
        );
    }

    /// <summary>Records the topout event. Idempotent — second call is ignored.</summary>
    public void RecordTopout(float simTime)
    {
        if (_toppedOut)
            return;
        _toppedOut = true;
        _events.Add(
            new EndlessReplayEvent { type = EndlessReplayEventType.Topout, simTime = simTime }
        );
    }
}

public enum EndlessReplayEventType
{
    Tap = 0,
    Topout = 1,
}

/// <summary>
/// Outcome of a tap, mirrors <see cref="TapResultKind"/> minus the
/// view-layer details. Stored in the replay so the verifier can detect
/// a player claiming the wrong outcome for a given cell + sim time.
/// </summary>
public enum EndlessTapKind
{
    Missed = 0,
    Blocked = 1,
    Cleared = 2,
}

/// <summary>
/// Single replay event. Flat shape for easy JSON round-tripping.
/// Newtonsoft serializes public fields by default.
/// </summary>
public sealed class EndlessReplayEvent
{
    public EndlessReplayEventType type;
    public float simTime;
    public int cellX;
    public int cellY;
    public EndlessTapKind tapKind;
}
