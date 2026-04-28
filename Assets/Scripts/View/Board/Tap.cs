using UnityEngine;

/// <summary>
/// Bundles the input + outcome of a single tap inside the board, surfaced
/// by <see cref="InputHandler"/> to the active game mode via the
/// <c>onTap</c> callback. Carries enough information for any mode to react:
/// classic for autosave + replay-event side effects, hardcore (planned)
/// for "blocked = loss", chain-effect modes for post-clear logic, etc.
///
/// InputHandler stays mode-agnostic: it does NOT touch <see cref="GameTimer"/>
/// or <see cref="ReplayRecorder"/>. Phase transitions, replay events,
/// autosave triggering all live in the mode's tap handler — which in turn
/// generally delegates to the mode's <c>Run</c> domain object.
/// </summary>
public readonly struct Tap
{
    /// <summary>The classification result from the view's clear attempt.</summary>
    public readonly TapResult Result;

    /// <summary>Cell that was tapped (always inside the board).</summary>
    public readonly Cell Cell;

    /// <summary>
    /// Float grid position of the tap (1 unit per cell, origin at the board
    /// corner). Identical numerically to the world-space position because
    /// <see cref="BoardCoords"/> is 1:1; recorded on replay events as
    /// <c>posX</c>/<c>posY</c>. Retains subcell precision.
    /// </summary>
    public readonly Vector3 GridPos;

    /// <summary>The arrow at the tapped cell, or <c>null</c> for <see cref="TapResult.Missed"/>.</summary>
    public readonly Arrow Arrow;

    /// <summary>Unix wall-clock seconds at the moment of the tap. Modes pass this to <see cref="GameTimer"/> for consistent phase transitions.</summary>
    public readonly double WallTimeSeconds;

    public Tap(TapResult result, Cell cell, Vector3 gridPos, Arrow arrow, double wallTimeSeconds)
    {
        Result = result;
        Cell = cell;
        GridPos = gridPos;
        Arrow = arrow;
        WallTimeSeconds = wallTimeSeconds;
    }

    /// <summary>True if the tap removed an arrow from the board.</summary>
    public bool IsClear => Result == TapResult.Cleared;
}
