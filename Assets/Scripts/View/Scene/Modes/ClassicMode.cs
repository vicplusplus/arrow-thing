using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Classic singleplayer mode: fixed-board speed-clear, save/replay enabled,
/// timer + victory popup on board cleared.
///
/// Phase 2D: ClassicMode now owns the full classic setup pipeline (parameter
/// resolution, snapshot/resume handling, board generation, board restore,
/// timer + recorder construction) and the victory wiring. <see cref="GameController"/>
/// is reduced to a thin orchestrator that resolves the active mode, awaits
/// <see cref="Setup"/>, then runs shared HUD/input wiring.
///
/// SerializeFields (victoryUIDocument, inspectionDuration,
/// inspectionWarningThreshold, editor board overrides) remain on
/// <see cref="GameController"/> because ClassicMode is added at runtime via
/// <c>AddComponent</c> and can't carry inspector-bound references. They're
/// exposed to ClassicMode through <c>internal</c> accessors.
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

    // Classic-owned run state (formerly on GameController).
    private GameTimer _timer;
    private ReplayRecorder _recorder;
    private string _gameId;
    private int _activeSeed;
    private int _w;
    private int _h;
    private int _maxLen;
    private float _inspectionDur;

    private const float FrameBudgetMs = 12f;

    public string Name => "Classic";

    /// <summary>Wired by GameController immediately after AddComponent.</summary>
    public void Bind(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public IEnumerator Setup(GameContext context) => RunSetup(context);

    public void Tick() { }

    public void OnHudWired()
    {
        var hudRoot = _controller.HudDocument?.rootVisualElement;
        if (hudRoot != null)
        {
            // Build the retry confirmation modal once the shared HUD is ready.
            // (Coop never gets here because OnHudWired only runs on the active
            // mode, and CoopMode hides the retry button entirely.)
            _retryModal = new ConfirmModal(hudRoot.Q("retry-modal"), "Retry?", "Retry", "Cancel");
            _retryModal.Confirmed += OnRetryConfirm;
            _retryModal.Cancelled += OnRetryCancel;
        }

        // Timer view is classic-only — coop has its own per-player timer.
        if (_timer != null)
        {
            var timerView = gameObject.AddComponent<GameTimerView>();
            timerView.Init(_timer, _controller.HudDocument, _controller.InspectionWarningThreshold);
        }
    }

    public Func<Cell, Vector3, bool> TapAttemptHandler => null;

    public void OnTapResult(TapResult result)
    {
        if (_recorder == null)
            return;

        // Any arrow tap (even Blocked) ends the inspection phase. Missed
        // (empty cell) doesn't — it's not engagement with the puzzle.
        if (result.Kind != TapResultKind.Missed && _timer != null && !_timer.IsSolving)
        {
            _timer.StartSolve(result.WallTimeSeconds);
            Debug.Log("[ClassicMode] Inspection ended — solve timer started");
            _recorder.RecordStartSolve();
        }

        switch (result.Kind)
        {
            case TapResultKind.Missed:
                _recorder.RecordMiss(result.WorldPos.x, result.WorldPos.y);
                break;
            case TapResultKind.Blocked:
                _recorder.RecordReject(result.WorldPos.x, result.WorldPos.y);
                break;
            case TapResultKind.Cleared:
            case TapResultKind.ClearedFirst:
                _recorder.RecordClear(result.WorldPos.x, result.WorldPos.y);
                MaybeAutosave();
                break;
            case TapResultKind.ClearedLast:
                _recorder.RecordClear(result.WorldPos.x, result.WorldPos.y);
                if (_timer != null)
                    _timer.Finish(result.WallTimeSeconds);
                Debug.Log(
                    $"[ClassicMode] Last arrow cleared — timer finished, solveElapsed={(_timer != null ? _timer.SolveElapsed.ToString("F3") : "N/A")}s"
                );
                // Save deletion + RecordEndSolve still fire from
                // BoardView.LastArrowClearing -> WireVictory's subscriber.
                break;
        }
    }

    private void MaybeAutosave()
    {
        if (!_autosaveEnabled || _timer == null)
            return;
        _clearsSinceLastSave++;
        if (_clearsSinceLastSave < AutosaveInterval)
            return;
        _clearsSinceLastSave = 0;
        int cleared = _initialArrowCount - _controller.CurrentBoard.Arrows.Count;
        Debug.Log(
            $"[ClassicMode] Autosave triggered: {cleared}/{_initialArrowCount} arrows cleared"
        );
        SaveManager.Save(BuildReplayData());
    }

    public void WireRunFlow() => WireVictory();

    public bool SupportsSaveOnLeave => true;

    public bool WouldOverwriteDifferentSave =>
        !_autosaveEnabled && HasAnyClearedArrows && SaveManager.HasSave();

    public bool HasInProgressChanges =>
        _autosaveEnabled && (_isContinuedGame || HasAnyClearedArrows);

    public Action OnQuickSaveHandler => OnQuickSave;

    public void SaveAndLeave()
    {
        if (_recorder != null && _timer != null)
        {
            _recorder.RecordSessionLeave();
            Debug.Log(
                $"[ClassicMode] SaveAndLeave: saving game id={_gameId}, arrows remaining={_controller.CurrentBoard?.Arrows.Count}"
            );
            SaveManager.Save(BuildReplayData());
        }
        _controller.RequestReturnToModeSelect();
    }

    public void Dispose() { }

    // ---- Public accessors (read by GameController for shared wiring) -------

    public List<List<Cell>> InitialBoardSnapshot => _initialBoardSnapshot;

    /// <summary>True while autosave should fire (no clobber risk).</summary>
    public bool AutosaveEnabled => _autosaveEnabled;

    // ---- Setup pipeline -----------------------------------------------------

    private IEnumerator RunSetup(GameContext context)
    {
        ResolveParameters(out ReplayData priorData, out bool deferredResume);

        string modeStr =
            deferredResume ? "deferred-resume"
            : priorData != null ? "resume"
            : "new";
        Debug.Log($"[ClassicMode] Setup: mode={modeStr}, board={_w}x{_h}, maxLen={_maxLen}");

        _controller.ShowLoadingInternal(deferredResume ? "Resuming..." : "Generating...");
        yield return null;

        if (deferredResume)
        {
            yield return LoadSaveAsync(result => priorData = result);
            if (priorData == null)
            {
                Debug.LogWarning(
                    "[ClassicMode] Deferred save load returned null — returning to MainMenu."
                );
                SceneNav.Pop();
                yield break;
            }
            Debug.Log(
                $"[ClassicMode] Save loaded: gameId={priorData.gameId}, board={priorData.boardWidth}x{priorData.boardHeight}, events={priorData.events.Count}"
            );
            ApplyResumeData(priorData);
        }

        ResolveSeed(priorData);
        bool hasSnapshot = priorData != null && !string.IsNullOrEmpty(priorData.boardSnapshot);

        var visualSettings = ThemeManager.Current ?? context.VisualSettings;
        (Board board, BoardView boardView) = BoardSetupHelper.CreateBoardAndView(
            _w,
            _h,
            visualSettings
        );
        context.Board = board;
        context.BoardView = boardView;

        CameraController camCtrl = null;
        if (context.MainCamera != null)
        {
            float? zoom = GameSettings.IsSet
                ? PlayerPrefs.GetFloat(GameSettings.ZoomSpeedPrefKey, GameSettings.DefaultZoomSpeed)
                : (float?)null;
            camCtrl = BoardSetupHelper.SetupCamera(context.MainCamera, board, zoom);
        }
        context.CameraController = camCtrl;
        boardView.SetCameraController(camCtrl);

        if (hasSnapshot)
        {
            SetInitialBoardSnapshot(priorData.GetSnapshotArrows());
            yield return RestoreBoard(board, boardView);
        }
        else
        {
            yield return GenerateBoard(board, boardView);
            if (board.Arrows.Count == 0)
            {
                // GenerateBoard logged + popped already.
                yield break;
            }
            // Hand a fresh snapshot to ClassicMode for save serialization.
            var snap = new List<List<Cell>>(board.Arrows.Count);
            foreach (Arrow arrow in board.Arrows)
                snap.Add(new List<Cell>(arrow.Cells));
            SetInitialBoardSnapshot(snap);
        }

        if (priorData != null)
        {
            bool resumeSolving = ReplayClears(
                board,
                boardView,
                priorData,
                out double resumeSolveElapsed
            );
            if (board.Arrows.Count == 0)
            {
                SaveManager.Delete();
                SceneNav.Pop();
                yield break;
            }
            SetupResumedRecorder(board, priorData);
            FinalizeSession(board, priorData);
            boardView.ApplyColoring();
            _controller.HideLoadingInternal();
            SetupTimer(resumeSolving, resumeSolveElapsed);
        }
        else
        {
            SetupNewRecorder(board);
            FinalizeSession(board, null);
            boardView.ApplyColoring();
            _controller.HideLoadingInternal();
            SetupTimer(false, 0.0);
        }
    }

    // ---- Parameter resolution ----------------------------------------------

    private void ResolveParameters(out ReplayData priorData, out bool deferredResume)
    {
        _w = _controller.EditorBoardWidth;
        _h = _controller.EditorBoardHeight;
        _maxLen = _controller.EditorMaxArrowLength;
        _inspectionDur = _controller.EditorInspectionDuration;
        priorData = null;
        deferredResume = false;

        if (!GameSettings.IsSet)
            return;

        if (GameSettings.IsResuming && GameSettings.ResumeData == null)
        {
            deferredResume = true;
            return;
        }

        _w = GameSettings.Width;
        _h = GameSettings.Height;
        _maxLen = GameSettings.MaxArrowLength;

        if (GameSettings.IsResuming)
        {
            priorData = GameSettings.ResumeData;
            _inspectionDur = priorData.inspectionDuration;
        }
    }

    private IEnumerator LoadSaveAsync(Action<ReplayData> onResult)
    {
        ReplayData loaded = null;
        yield return SaveManager.LoadAsync(d => loaded = d);
        onResult(loaded);
    }

    private void ApplyResumeData(ReplayData data)
    {
        GameSettings.SetResumeData(data);
        _w = GameSettings.Width;
        _h = GameSettings.Height;
        _maxLen = GameSettings.MaxArrowLength;
        _inspectionDur = data.inspectionDuration;
    }

    private void ResolveSeed(ReplayData priorData)
    {
        _activeSeed =
            (priorData != null) ? priorData.seed
            : (GameSettings.IsSet || _controller.EditorUseRandomSeed) ? Environment.TickCount
            : _controller.EditorSeed;
    }

    // ---- Board work coroutines ---------------------------------------------

    private IEnumerator RestoreBoard(Board board, BoardView boardView)
    {
        int totalArrows = _initialBoardSnapshot?.Count ?? 0;
        Debug.Log($"[ClassicMode] RestoreBoard: restoring {totalArrows} arrows from snapshot");
        int totalSteps = totalArrows * 2;
        var restorer = BoardSetupHelper.RestoreBoardFromSnapshot(
            board,
            boardView,
            _initialBoardSnapshot,
            FrameBudgetMs
        );

        while (restorer.MoveNext())
        {
            if (_controller.CancelRequested)
            {
                SceneNav.Pop();
                yield break;
            }
            _controller.SetLoadProgress((float)restorer.Current / totalSteps);
            yield return null;
        }
    }

    private IEnumerator GenerateBoard(Board board, BoardView boardView)
    {
        var generator = BoardGeneration.FillBoardIncremental(board, _maxLen, _activeSeed);

        var weights = GenerationProgress.ClientWeights;
        var spawnedArrows = new List<Arrow>();
        int arrowsBeforeCompaction = 0;
        var phase = GenerationPhase.Generating;
        float compactStartRealtime = 0f;
        bool rebuildingViews = false;
        List<Arrow> rebuildList = null;
        int rebuildIndex = 0;
        int rebuildTotal = 0;

        while (true)
        {
            if (_controller.CancelRequested)
            {
                (generator as System.IDisposable)?.Dispose();
                SceneNav.Pop();
                yield break;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool done = false;
            while (sw.ElapsedMilliseconds < FrameBudgetMs)
            {
                if (rebuildingViews)
                {
                    if (rebuildIndex < rebuildTotal)
                    {
                        boardView.AddArrowView(rebuildList[rebuildIndex]);
                        spawnedArrows.Add(rebuildList[rebuildIndex]);
                        rebuildIndex++;
                        continue;
                    }
                    rebuildingViews = false;
                    rebuildList = null;
                }

                if (!generator.MoveNext())
                {
                    done = true;
                    break;
                }

                if (generator.Current is GenerationPhase nextPhase)
                {
                    if (nextPhase == GenerationPhase.Compacting)
                    {
                        arrowsBeforeCompaction = board.Arrows.Count;
                        compactStartRealtime = Time.realtimeSinceStartup;
                    }
                    else if (
                        nextPhase == GenerationPhase.Finalizing
                        && phase == GenerationPhase.Compacting
                    )
                    {
                        foreach (Arrow a in spawnedArrows)
                            boardView.RemoveArrowView(a);
                        spawnedArrows.Clear();
                        rebuildList = new List<Arrow>(board.Arrows);
                        rebuildIndex = 0;
                        rebuildTotal = rebuildList.Count;
                        rebuildingViews = true;
                    }
                    phase = nextPhase;
                }
                else if (phase == GenerationPhase.Generating)
                {
                    Arrow arrow = board.Arrows[spawnedArrows.Count];
                    boardView.AddArrowView(arrow);
                    spawnedArrows.Add(arrow);
                }
            }

            float progress;
            if (rebuildingViews)
            {
                float rebuildRatio = rebuildTotal > 0 ? (float)rebuildIndex / rebuildTotal : 1f;
                progress = GenerationProgress.ForRebuildingViews(rebuildRatio, weights);
            }
            else
            {
                switch (phase)
                {
                    case GenerationPhase.Generating:
                    {
                        int initial = board.InitialCandidateCount;
                        int remaining = board.RemainingCandidateCount;
                        float depletion = initial > 0 ? 1f - (float)remaining / initial : 0f;
                        progress = GenerationProgress.ForGenerating(depletion, weights);
                        break;
                    }
                    case GenerationPhase.Compacting:
                    {
                        float expectedCompactSec = GenerationProgress.ExpectedCompactSeconds(
                            arrowsBeforeCompaction
                        );
                        float elapsed = Time.realtimeSinceStartup - compactStartRealtime;
                        float timeRatio =
                            expectedCompactSec > 0.001f ? elapsed / expectedCompactSec : 1f;
                        progress = GenerationProgress.ForCompacting(
                            mergesCompleted: arrowsBeforeCompaction - board.Arrows.Count,
                            arrowsBeforeCompaction: arrowsBeforeCompaction,
                            timeRatio: timeRatio,
                            w: weights
                        );
                        break;
                    }
                    case GenerationPhase.Finalizing:
                    {
                        int arrowCount = board.Arrows.Count;
                        float finalizeRatio =
                            arrowCount > 0 && generator.Current is int finalized
                                ? (float)finalized / arrowCount
                                : 0f;
                        progress = GenerationProgress.ForFinalizing(finalizeRatio, weights);
                        break;
                    }
                    default:
                        progress = 0f;
                        break;
                }
            }
            _controller.SetLoadProgress(progress);

            if (done)
                break;
            yield return null;
        }

        if (board.Arrows.Count == 0)
        {
            Debug.LogWarning(
                $"[ClassicMode] GenerateBoard produced 0 arrows (board {_w}x{_h}, maxLen={_maxLen}, seed={_activeSeed}). Returning to menu."
            );
            SceneNav.Pop();
            yield break;
        }
        Debug.Log(
            $"[ClassicMode] GenerateBoard complete: {board.Arrows.Count} arrows, board={_w}x{_h}, seed={_activeSeed}"
        );
    }

    // ---- Resume / recorder / timer -----------------------------------------

    private bool ReplayClears(
        Board board,
        BoardView boardView,
        ReplayData priorData,
        out double solveElapsed
    )
    {
        bool solving = false;
        int totalClearEvents = 0;
        int successfulClears = 0;
        foreach (ReplayEvent evt in priorData.events)
        {
            if (evt.type == ReplayEventType.Clear)
            {
                totalClearEvents++;
                var worldPos = new Vector3(evt.posX ?? 0f, evt.posY ?? 0f, 0f);
                Cell cell = BoardCoords.WorldToCell(worldPos, board.Width, board.Height);
                if (board.Contains(cell))
                {
                    Arrow arrow = board.GetArrowAt(cell);
                    if (arrow != null && board.IsClearable(arrow))
                    {
                        boardView.RemoveArrowView(arrow);
                        board.RemoveArrow(arrow);
                        successfulClears++;
                    }
                    else if (arrow == null)
                        Debug.LogWarning(
                            $"[ClassicMode] ReplayClears: no arrow at cell ({cell.X},{cell.Y}) for clear event seq={evt.seq}"
                        );
                    else
                        Debug.LogWarning(
                            $"[ClassicMode] ReplayClears: arrow at ({cell.X},{cell.Y}) not clearable for clear event seq={evt.seq}"
                        );
                }
                else
                {
                    Debug.LogWarning(
                        $"[ClassicMode] ReplayClears: clear event seq={evt.seq} maps to out-of-bounds cell ({cell.X},{cell.Y})"
                    );
                }
            }
            if (evt.type == ReplayEventType.StartSolve)
                solving = true;
        }
        solveElapsed = priorData.ComputedSolveElapsed;
        Debug.Log(
            $"[ClassicMode] ReplayClears: {successfulClears}/{totalClearEvents} clears applied, solving={solving}, solveElapsed={solveElapsed:F3}s"
        );
        return solving;
    }

    private void SetupResumedRecorder(Board board, ReplayData priorData)
    {
        _gameId = priorData.gameId;
        int nextSeq =
            priorData.events.Count > 0 ? priorData.events[priorData.events.Count - 1].seq + 1 : 0;
        _recorder = new ReplayRecorder(priorData.events, nextSeq);
        _recorder.RecordSessionRejoin();
        Debug.Log(
            $"[ClassicMode] Resumed game: id={_gameId}, nextSeq={nextSeq}, remainingArrows={board.Arrows.Count}"
        );
    }

    private void SetupNewRecorder(Board board)
    {
        _gameId = Guid.NewGuid().ToString();
        _recorder = new ReplayRecorder();
        _recorder.RecordSessionStart();
        Debug.Log(
            $"[ClassicMode] New game: id={_gameId}, arrows={board.Arrows.Count}, board={_w}x{_h}, seed={_activeSeed}"
        );
    }

    private void SetupTimer(bool resumeSolving, double resumeSolveElapsed)
    {
        _timer = new GameTimer(_inspectionDur);
        double wallNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (resumeSolving)
        {
            _timer.Resume(wallNow, resumeSolveElapsed);
            Debug.Log($"[ClassicMode] Timer resumed: priorElapsed={resumeSolveElapsed:F3}s");
        }
        else
        {
            _timer.Start(wallNow);
            Debug.Log($"[ClassicMode] Timer started fresh: inspectionDuration={_inspectionDur}s");
        }
    }

    /// <summary>
    /// Captures the initial arrow count + autosave-enable decision. Called
    /// after recorder + initial board state exist. Takes the board directly
    /// (rather than going through <c>_controller.CurrentBoard</c>) because
    /// the controller's board field isn't assigned until after Setup returns.
    /// </summary>
    public void FinalizeSession(Board board, ReplayData priorData)
    {
        _initialArrowCount = board.Arrows.Count;
        // Autosave enabled when there's no existing save (we can write fresh)
        // OR we're continuing (we own that save). Otherwise disabled to avoid
        // clobbering another save.
        _autosaveEnabled = !SaveManager.HasSave() || priorData != null;
        _isContinuedGame = priorData != null;
        _clearsSinceLastSave = 0;
    }

    /// <summary>
    /// Captures the initial board snapshot for the replay save. Called either
    /// directly during snapshot resume or after fresh generation completes.
    /// </summary>
    public void SetInitialBoardSnapshot(List<List<Cell>> snapshot)
    {
        _initialBoardSnapshot = snapshot;
    }

    /// <summary>
    /// Builds the replay payload for save/score submission. Reads board
    /// shape + recorder events + timer state from this mode + the controller.
    /// </summary>
    public ReplayData BuildReplayData()
    {
        return _recorder.ToReplayData(
            _gameId,
            _activeSeed,
            _w,
            _h,
            _maxLen,
            _inspectionDur,
            boardSnapshot: _initialBoardSnapshot,
            gameVersion: UnityEngine.Application.version
        );
    }

    // ---- Victory wiring ----------------------------------------------------

    private void WireVictory()
    {
        var victoryDoc = _controller.VictoryDocument;
        if (victoryDoc == null || !victoryDoc.enabled || victoryDoc.rootVisualElement == null)
            return;

        var boardView = _controller.BoardViewRef;
        var camCtrl = _controller.CameraControllerRef;
        var hudDoc = _controller.HudDocument;

        var victory = gameObject.AddComponent<VictoryController>();
        victory.Init(
            victoryDoc,
            boardView.GridRenderer,
            camCtrl,
            _w,
            _h,
            _timer,
            hudDoc,
            BuildReplayData
        );
        boardView.LastArrowClearing += () =>
        {
            _controller.MarkVictoryStarted();
            var input = _controller.ActiveInputHandler;
            if (input != null)
                input.SetInputEnabled(false);
            var backBtn = _controller.BackButton;
            if (backBtn != null)
                backBtn.style.display = DisplayStyle.None;
            var retryBtn = _controller.HudRetryButton;
            if (retryBtn != null)
                retryBtn.style.display = DisplayStyle.None;
            if (_recorder != null)
                _recorder.RecordEndSolve();
            if (_autosaveEnabled)
                SaveManager.Delete();
            victory.OnLastArrowClearing();
        };
        boardView.BoardCleared += victory.OnBoardCleared;
    }

    // ---- Retry handlers ----------------------------------------------------

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
        if (_recorder != null)
            SaveManager.Save(BuildReplayData());
    }

    public void OnRetryClickedExternal() => OnRetryClicked();

    private bool HasAnyClearedArrows =>
        _controller.CurrentBoard != null
        && _controller.CurrentBoard.Arrows.Count < _initialArrowCount;
}
