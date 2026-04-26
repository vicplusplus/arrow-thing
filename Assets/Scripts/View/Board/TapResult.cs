using UnityEngine;

/// <summary>
/// Outcome of a single tap inside the board, surfaced by
/// <see cref="InputHandler"/> to the active game mode via the
/// <c>onTapResult</c> callback. Carries enough information for any mode to
/// react: classic for autosave + replay recording, hardcore for "blocked =
/// loss", chain-effect modes for post-clear logic, etc.
///
/// InputHandler stays mode-agnostic: it does NOT touch <see cref="GameTimer"/>
/// or <see cref="ReplayRecorder"/>. Phase transitions (inspection→solving),
/// replay events (Miss/Clear/Reject/StartSolve), and autosave triggering all
/// live in the mode's tap-result handler.
/// </summary>
public readonly struct TapResult
{
    public readonly TapResultKind Kind;

    /// <summary>Cell that was tapped (always inside the board).</summary>
    public readonly Cell Cell;

    /// <summary>World-space tap position (used by replay events for posX/posY).</summary>
    public readonly Vector3 WorldPos;

    /// <summary>The arrow at the tapped cell, or <c>null</c> for <see cref="TapResultKind.Missed"/>.</summary>
    public readonly Arrow Arrow;

    /// <summary>Unix wall-clock seconds at the moment of the tap. Modes pass this to <see cref="GameTimer"/> for consistent phase transitions.</summary>
    public readonly double WallTimeSeconds;

    public TapResult(
        TapResultKind kind,
        Cell cell,
        Vector3 worldPos,
        Arrow arrow,
        double wallTimeSeconds
    )
    {
        Kind = kind;
        Cell = cell;
        WorldPos = worldPos;
        Arrow = arrow;
        WallTimeSeconds = wallTimeSeconds;
    }

    /// <summary>True if the tap removed an arrow (any of Cleared / ClearedFirst / ClearedLast).</summary>
    public bool IsClear =>
        Kind == TapResultKind.Cleared
        || Kind == TapResultKind.ClearedFirst
        || Kind == TapResultKind.ClearedLast;
}

/// <summary>
/// Kinds of tap outcomes. Mirrors <see cref="ClearResult"/> plus a
/// <see cref="Missed"/> case for taps on empty cells (which never reach
/// <c>BoardView.TryClearArrow</c>).
/// </summary>
public enum TapResultKind
{
    /// <summary>Tap hit an empty cell. No arrow involved.</summary>
    Missed,

    /// <summary>Arrow was tapped but is currently blocked by dependencies.</summary>
    Blocked,

    /// <summary>Arrow was cleared. Not the first or last on the board.</summary>
    Cleared,

    /// <summary>Arrow was cleared. It was the first arrow cleared this run (kicks off animations).</summary>
    ClearedFirst,

    /// <summary>Arrow was cleared. It was the last arrow on the board (win condition for classic).</summary>
    ClearedLast,
}
