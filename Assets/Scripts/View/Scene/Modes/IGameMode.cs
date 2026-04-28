using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// A self-contained game mode. Each mode (Classic, Coop, future Endless/PvP)
/// owns its own HUD additions, result screen, generation/snapshot strategy,
/// win/loss condition, and per-frame logic.
///
/// <see cref="GameController"/> is a thin orchestrator that:
///   1. Picks an active mode based on <c>GameSettings</c>.
///   2. Wires shared infrastructure (FocusNavigator, leave modal, loading
///      overlay, theme hooks).
///   3. Delegates everything mode-specific to the active <see cref="IGameMode"/>.
///
/// Modes are typically <see cref="MonoBehaviour"/> components on the same
/// GameObject as GameController so they can carry SerializeFields and use
/// Unity coroutines.
/// </summary>
public interface IGameMode
{
    /// <summary>Human-readable mode name for logs / analytics.</summary>
    string Name { get; }

    /// <summary>
    /// Mode-specific setup: produce the <see cref="Board"/>, <see cref="BoardView"/>,
    /// <see cref="CameraController"/>, and any mode-only resources (recorder,
    /// session, sidebar, etc). Yields incremental progress; the host calls
    /// <see cref="GameContext.UpdateLoadingProgress"/> to drive the loading bar.
    /// May yield <see cref="ModeSetupResult.Cancelled"/> to abort cleanly.
    /// </summary>
    IEnumerator Setup(GameContext context);

    /// <summary>
    /// Per-frame tick. Mode runs heartbeat, reconnect drivers, spawn timers,
    /// etc. here. GameController forwards <see cref="MonoBehaviour.Update"/>
    /// to the active mode after running shared per-frame logic
    /// (FocusNavigator, Escape handling, loading overlay).
    /// </summary>
    void Tick();

    /// <summary>
    /// Called after <see cref="GameController"/> has wired the shared HUD
    /// (back button, leave modal, loading overlay). Modes use this hook to
    /// hide elements they don't want (e.g. coop hides retry) or to inject
    /// their own UXML overlay into the HUD root.
    /// </summary>
    void OnHudWired();

    /// <summary>
    /// Optional tap handler injected into <see cref="InputHandler"/>. Coop
    /// returns its remote-broadcast handler; classic and others return null
    /// to use the default tap-to-clear behavior.
    /// </summary>
    Func<Cell, Vector3, bool> TapAttemptHandler { get; }

    /// <summary>
    /// Called for every tap that reaches the board's bounds, with the
    /// outcome (Missed / Blocked / Cleared). Classic uses this to drive
    /// timer phase transitions, replay-event recording, and autosave —
    /// generally by forwarding the tap into the mode's <c>Run</c> domain
    /// object. Hardcore (planned) flips to a death screen on
    /// <see cref="TapResult.Blocked"/>. Coop ignores it (the
    /// <see cref="TapAttemptHandler"/> short-circuits before this fires).
    /// </summary>
    void OnTap(Tap tap);

    /// <summary>
    /// Wires the end-of-run flow. Classic instantiates <see cref="VictoryController"/>
    /// and hooks board-cleared events. Coop skips entirely (server-driven
    /// completion). Endless wires topout. Called once after
    /// <see cref="GameController.WireHud"/> + <see cref="GameController.WireInput"/>.
    /// </summary>
    void WireRunFlow();

    /// <summary>
    /// True if the mode supports a "save and leave" option in the leave modal.
    /// Classic returns true (saves replay state); coop returns false (no
    /// client-side save). The host shared leave modal asks the mode this
    /// before showing the Save & Leave button.
    /// </summary>
    bool SupportsSaveOnLeave { get; }

    /// <summary>
    /// True if leaving the run right now would overwrite a different existing
    /// save. The leave modal warns the player in that case. Classic returns
    /// true if there's a save on disk and the run hasn't been autosaving;
    /// coop returns false.
    /// </summary>
    bool WouldOverwriteDifferentSave { get; }

    /// <summary>
    /// True if the current run has unsaved progress worth persisting on
    /// leave. Classic returns true when autosave is enabled AND the player
    /// has either cleared arrows or is continuing a saved game. Coop returns
    /// false (no client-side save).
    /// </summary>
    bool HasInProgressChanges { get; }

    /// <summary>
    /// Optional handler invoked by <see cref="InputHandler"/> on the
    /// quick-save keybind (Ctrl+S). Classic writes the current state to disk;
    /// coop returns null (no quick-save action).
    /// </summary>
    Action OnQuickSaveHandler { get; }

    /// <summary>
    /// Performs the mode's "save and leave" action: persist any in-progress
    /// state (replay, partial board) and pop the scene. Classic writes the
    /// save to disk then pops; coop just pops.
    /// </summary>
    void SaveAndLeave();

    /// <summary>
    /// Mode-specific cleanup. Called from <see cref="GameController.OnDestroy"/>
    /// before shared cleanup (FocusNavigator dispose, theme unsubscribe).
    /// </summary>
    void Dispose();
}
