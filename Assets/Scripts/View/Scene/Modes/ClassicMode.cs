using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Classic singleplayer mode: fixed-board speed-clear, save/replay enabled,
/// timer + victory popup on board cleared.
///
/// Phase 2 of the per-mode encapsulation refactor: this class owns the
/// autosave / save-and-leave / retry-modal cluster. Setup pipeline (timer,
/// recorder, board fill, victory wiring) still lives on
/// <see cref="GameController"/>; <see cref="FinalizeSession"/> is the
/// integration point — GameController calls it after recorder + initial
/// board state exist, and from there ClassicMode owns the autosave state.
///
/// Subsequent phases will absorb the rest:
/// timer/recorder/SetupTimer/SetupNewRecorder/SetupResumedRecorder,
/// WireVictoryDefault, GenerateAndSetup classic branch, RestoreBoard,
/// ResolveParameters/Seed, ApplyResumeData, ReplayClears.
/// </summary>
public sealed class ClassicMode : MonoBehaviour, IGameMode
{
    private GameController _controller;

    // Autosave / save-state cluster (owned by ClassicMode).
    private bool _autosaveEnabled;
    private bool _isContinuedGame;
    private int _initialArrowCount;
    private int _clearsSinceLastSave;
    private List<List<Cell>> _initialBoardSnapshot;
    private const int AutosaveInterval = 10;

    // Retry confirmation modal — classic-only (coop hides retry button entirely).
    private ConfirmModal _retryModal;

    public string Name => "Classic";

    /// <summary>Wired by GameController immediately after AddComponent.</summary>
    public void Bind(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public IEnumerator Setup(GameContext context) => _controller.RunClassicSetupCoroutine(context);

    public void Tick() { }

    public void OnHudWired()
    {
        // Build the retry confirmation modal once the shared HUD is ready.
        // (Coop never gets here because OnHudWired only runs on the active
        // mode, and CoopMode hides the retry button entirely.)
        var hudRoot = _controller.HudDocument?.rootVisualElement;
        if (hudRoot != null)
        {
            _retryModal = new ConfirmModal(hudRoot.Q("retry-modal"), "Retry?", "Retry", "Cancel");
            _retryModal.Confirmed += OnRetryConfirm;
            _retryModal.Cancelled += OnRetryCancel;
        }
    }

    public Func<Cell, Vector3, bool> TapAttemptHandler => null;

    public void OnArrowCleared()
    {
        // Autosave: every Nth cleared arrow, persist current state.
        if (!_autosaveEnabled || _controller.Recorder == null || _controller.Timer == null)
            return;

        _clearsSinceLastSave++;
        if (_clearsSinceLastSave >= AutosaveInterval)
        {
            _clearsSinceLastSave = 0;
            int cleared = _initialArrowCount - _controller.CurrentBoard.Arrows.Count;
            Debug.Log(
                $"[ClassicMode] Autosave triggered: {cleared}/{_initialArrowCount} arrows cleared"
            );
            SaveManager.Save(BuildReplayData());
        }
    }

    public void WireRunFlow() => _controller.WireClassicVictory();

    public bool SupportsSaveOnLeave => true;

    public bool WouldOverwriteDifferentSave =>
        !_autosaveEnabled && HasAnyClearedArrows && SaveManager.HasSave();

    public bool HasInProgressChanges =>
        _autosaveEnabled && (_isContinuedGame || HasAnyClearedArrows);

    public Action OnQuickSaveHandler => OnQuickSave;

    public void SaveAndLeave()
    {
        if (_controller.Recorder != null && _controller.Timer != null)
        {
            _controller.Recorder.RecordSessionLeave();
            Debug.Log(
                $"[ClassicMode] SaveAndLeave: saving game id={_controller.GameId}, arrows remaining={_controller.CurrentBoard?.Arrows.Count}"
            );
            SaveManager.Save(BuildReplayData());
        }
        _controller.RequestReturnToModeSelect();
    }

    public void Dispose() { }

    // ---- Phase-2 integration points called by GameController ----

    /// <summary>
    /// Called by <see cref="GameController"/> after recorder + initial board
    /// state exist. Captures the initial arrow count so autosave / leave
    /// logic knows whether the player has made progress.
    /// </summary>
    public void FinalizeSession(ReplayData priorData)
    {
        _initialArrowCount = _controller.CurrentBoard.Arrows.Count;
        // Autosave enabled when there's no existing save (we can write fresh)
        // OR we're continuing (we own that save). Otherwise disabled to avoid
        // clobbering another save.
        _autosaveEnabled = !SaveManager.HasSave() || priorData != null;
        _isContinuedGame = priorData != null;
        _clearsSinceLastSave = 0;
    }

    /// <summary>
    /// Captures the initial board snapshot for the replay save. Called by
    /// GameController after generation completes (only for fresh runs;
    /// resumed runs inherit the snapshot from <c>priorData</c>).
    /// </summary>
    public void SetInitialBoardSnapshot(List<List<Cell>> snapshot)
    {
        _initialBoardSnapshot = snapshot;
    }

    public List<List<Cell>> InitialBoardSnapshot => _initialBoardSnapshot;

    /// <summary>True while autosave should fire (no clobber risk). Read by GameController.WireVictoryDefault to decide whether to delete the save on victory.</summary>
    public bool AutosaveEnabled => _autosaveEnabled;

    /// <summary>
    /// Builds the replay payload for save/score submission. Reads board
    /// shape + recorder events + timer state from <see cref="GameController"/>.
    /// </summary>
    public ReplayData BuildReplayData()
    {
        return _controller.Recorder.ToReplayData(
            _controller.GameId,
            _controller.ActiveSeed,
            _controller.Width,
            _controller.Height,
            _controller.MaxArrowLength,
            _controller.InspectionDuration,
            boardSnapshot: _initialBoardSnapshot,
            gameVersion: UnityEngine.Application.version
        );
    }

    // ---- Retry handlers ----

    private void OnRetryClicked()
    {
        if (HasAnyClearedArrows)
        {
            _retryModal?.Show();
            if (_controller.ActiveInputHandler != null)
                _controller.ActiveInputHandler.SetInputEnabled(false);
        }
        else
        {
            _controller.RequestQuickReset();
        }
    }

    private void OnRetryConfirm()
    {
        _retryModal?.Hide();
        _controller.RequestQuickReset();
    }

    private void OnRetryCancel()
    {
        _retryModal?.Hide();
        if (_controller.ActiveInputHandler != null)
            _controller.ActiveInputHandler.SetInputEnabled(true);
    }

    private void OnQuickSave()
    {
        if (_controller.Recorder != null)
            SaveManager.Save(BuildReplayData());
    }

    public void OnRetryClickedExternal() => OnRetryClicked();

    private bool HasAnyClearedArrows =>
        _controller.CurrentBoard != null
        && _controller.CurrentBoard.Arrows.Count < _initialArrowCount;
}
