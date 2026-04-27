using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Endless mode: starts with an empty board, then a continuous push-tick loop
/// spawns pending arrows the player must clear before the next push or topout.
/// No initial fill — the board grows from nothing as garbage accumulates,
/// like Tetris.
///
/// EndlessMode is the <see cref="IGameMode"/> adapter. The heavy lifting
/// (push-tick scheduling, garbage-meter UI, danger tint, topout detection,
/// commit pipeline) lives on <see cref="EndlessModeController"/>, a sibling
/// MonoBehaviour added during <see cref="Setup"/>. Splitting the controller
/// out keeps its <c>[SerializeField]</c> tuning surface intact (defaults
/// apply at runtime) and isolates the per-frame loop from mode lifecycle.
///
/// No save / replay / leaderboard — endless runs are too real-time to be
/// resumable, and end-of-run scoring is local-only for now (see
/// <see cref="EndlessResultController"/>).
/// </summary>
public sealed class EndlessMode : MonoBehaviour, IGameMode
{
    private GameController _controller;
    private EndlessModeController _endless;

    /// <summary>
    /// Default endless board size when launched outside the menu (editor
    /// playthrough). The menu picker writes to <see cref="GameSettings.Width"/>
    /// directly, so this only kicks in for direct-scene-load.
    /// </summary>
    private const int DefaultBoardSize = 20;

    private int _w;
    private int _h;
    private int _activeSeed;
    private string _gameId;

    /// <summary>
    /// Captures every player tap (and the topout) for server-side replay
    /// verification. Built once in <see cref="RunSetup"/>; payload emitted
    /// at topout (Phase 2a: Debug.Log only; Phase 2c: POST to the server).
    /// Shared <see cref="ReplayRecorder"/> type — endless writes via the
    /// cell+simTime variant methods so verification doesn't need world↔cell
    /// conversion or wall-clock involvement.
    /// </summary>
    private ReplayRecorder _recorder;

    public string Name => "Endless";

    public void Bind(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public IEnumerator Setup(GameContext context) => RunSetup(context);

    public void Tick()
    {
        // EndlessModeController has its own Update (preview→commit pipeline,
        // push-tick driver, danger tint). Nothing to forward here.
    }

    public void OnHudWired()
    {
        // Result screen owns retry once the run ends; no in-game retry.
        var retryBtn = _controller.HudRetryButton;
        if (retryBtn != null)
            retryBtn.style.display = DisplayStyle.None;

        // Hide the classic timer label — endless owns its own cleared-count
        // label in EndlessHud.uxml. (No GameTimerView is built for endless,
        // so the label sits at its default text without this hide.)
        var timerLabel = _controller.HudTimerLabel;
        if (timerLabel != null)
            timerLabel.style.display = DisplayStyle.None;
    }

    public Func<Cell, Vector3, bool> TapAttemptHandler => null;

    public void OnTap(Tap tap)
    {
        // Endless routes real-arrow clears through EndlessBoardSession via
        // BoardView.SetArrowRemover (wired in Setup). Stats + immediate-mode
        // shortfall placement happen inside EndlessModeController.HandleRealArrowCleared.
        //
        // We DO record every tap (including misses + blocked) for replay
        // verification: blocked/missed taps affect _lastClearTime / combo-
        // streak state, so a faithful replay needs them too. Endless passes
        // its sim-time on the recorder call so the verifier can reproduce
        // the time-driven loop deterministically.
        if (_endless == null || _recorder == null)
            return;
        float simTime = _endless.SimTime;
        switch (tap.Result)
        {
            case TapResult.Cleared:
                _recorder.RecordClear(tap.GridPos.x, tap.GridPos.y, simTime);
                break;
            case TapResult.Blocked:
                _recorder.RecordReject(tap.GridPos.x, tap.GridPos.y, simTime);
                break;
            case TapResult.Missed:
                _recorder.RecordMiss(tap.GridPos.x, tap.GridPos.y, simTime);
                break;
        }
    }

    public void WireRunFlow()
    {
        // Topout subscriptions wired in Setup once the controller exists.
        // Nothing additional to do here.
    }

    public bool SupportsSaveOnLeave => false;
    public bool WouldOverwriteDifferentSave => false;
    public bool HasInProgressChanges => false;
    public Action OnQuickSaveHandler => null;

    public void SaveAndLeave()
    {
        // Endless runs aren't saveable — just pop.
        SceneNav.Pop();
    }

    public void Dispose()
    {
        if (_endless != null)
        {
            _endless.ToppedOut -= OnToppedOut;
            _endless.ResultReady -= OnResultReady;
        }
    }

    // ---- Setup pipeline -----------------------------------------------------

    private IEnumerator RunSetup(GameContext context)
    {
        ResolveParameters();

        Debug.Log($"[EndlessMode] Setup: board={_w}x{_h}, seed={_activeSeed}");

        _controller.ShowLoadingInternal("Loading...");
        yield return null;

        // Empty board — endless grows it from nothing via the push-tick loop.
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

        _controller.HideLoadingInternal();

        // Construct the endless controller now that board+view+camera exist.
        // It needs the HUD root to mount its meter UI, so it must run after
        // ResolveHudElements has populated the HUD references on the
        // controller (which the orchestrator does before invoking Setup).
        _endless = gameObject.AddComponent<EndlessModeController>();

        var hudRoot = _controller.HudDocument?.rootVisualElement;
        if (hudRoot != null && _controller.EndlessHudAsset != null)
        {
            // Inject endless-mode-specific HUD overlay (garbage meter +
            // cleared-count label) into the shared HUD root. Owned entirely
            // by endless — classic and co-op never see these elements.
            _controller.EndlessHudAsset.CloneTree(hudRoot);
        }

        _endless.Initialize(
            board,
            boardView,
            spawnSeed: _activeSeed,
            hudRoot: hudRoot,
            camera: context.MainCamera
        );

        // Route real-arrow clears through the session so its NativeGenerationState
        // (used for ghost cycle detection) sees the cleared arrow vanish.
        boardView.SetArrowRemover(_endless.HandleRealArrowCleared);

        // Replay capture: a fresh recorder per run, paired with a fresh game
        // ID so server-side dedup can spot retried submissions.
        _gameId = Guid.NewGuid().ToString();
        _recorder = new ReplayRecorder();

        // Topout signal: ToppedOut fires immediately to freeze gameplay so
        // the player can't keep tapping during the pause; ResultReady fires
        // after a short delay so the player can see the final saturated
        // board state before the modal appears.
        _endless.ToppedOut += OnToppedOut;
        _endless.ResultReady += OnResultReady;
    }

    private void ResolveParameters()
    {
        if (GameSettings.IsSet)
        {
            // Menu picker writes Width/Height. Endless presets are square
            // (10/20/40), but we don't enforce that here — the controller
            // is happy with non-square if a future variant uses one.
            _w = GameSettings.Width;
            _h = GameSettings.Height;
        }
        else
        {
            _w = DefaultBoardSize;
            _h = DefaultBoardSize;
        }
        // Endless boards always use a fresh random seed when launched from
        // the menu. Editor playthrough honors the editor seed toggle so
        // dev iterations on a specific board are possible.
        _activeSeed =
            (GameSettings.IsSet || _controller.EditorUseRandomSeed)
                ? Environment.TickCount
                : _controller.EditorSeed;
    }

    // ---- End-of-run sequence -----------------------------------------------

    private void OnToppedOut()
    {
        Debug.Log(
            $"[EndlessMode] Topped out — clears={_endless.ClearCount}, "
                + $"longestCombo={_endless.LongestCombo}, "
                + $"duration={_endless.RunDurationSeconds:F1}s"
        );

        // Capture the topout in the replay log, then both:
        //   1. Persist to the local endless leaderboard so the player's PB +
        //      history are visible immediately (and stay around when the
        //      server is offline / pending / rejects the submission).
        //   2. Submit to the server for verification. EndlessScoreSubmitter
        //      is fire-and-forget — toast notifications announce success /
        //      failure / pending verification asynchronously so the result
        //      screen can come up immediately.
        if (_recorder != null && _endless != null)
        {
            _recorder.RecordTopout(_endless.SimTime);
            ReplayData payload = BuildReplayPayload();
            EndlessLeaderboardManager.Instance?.RecordEndlessResult(payload);
            EndlessScoreSubmitter.Submit(payload);
        }

        // Freeze gameplay: input off, shared HUD chrome hidden. Player sees
        // the saturated board with the danger tint locked at full red until
        // the result screen appears.
        _controller.DisableGameplayHudAndInput();

        // Hide endless-specific HUD overlays too — meter and cleared-count
        // label aren't useful once the run is over and would otherwise
        // bleed through behind the result screen.
        var hudRoot = _controller.HudDocument?.rootVisualElement;
        if (hudRoot != null)
        {
            var meter = hudRoot.Q<VisualElement>("endless-meter");
            if (meter != null)
                meter.style.display = DisplayStyle.None;
            var clearsLabel = hudRoot.Q<Label>("endless-clears-label");
            if (clearsLabel != null)
                clearsLabel.style.display = DisplayStyle.None;
        }
    }

    /// <summary>
    /// Builds the unified <see cref="ReplayData"/> payload from the recorder
    /// + final stats. Pure construction — does not write to disk or send.
    /// </summary>
    private ReplayData BuildReplayPayload()
    {
        return new ReplayData
        {
            gameId = _gameId,
            mode = GameMode.Endless,
            gameVersion = Application.version,
            seed = _activeSeed,
            boardWidth = _w,
            boardHeight = _h,
            // maxArrowLength + inspectionDuration left at default (0 / 0)
            // — endless doesn't have a player-set max length and there's
            // no inspection phase. Verifier dispatches on `mode`.
            tuningsVersion = 1,
            events = new List<ReplayEvent>(_recorder.Events),
            clears = _endless.ClearCount,
            longestCombo = _endless.LongestCombo,
            durationSeconds = _endless.RunDurationSeconds,
        };
    }

    private void OnResultReady()
    {
        var victoryDoc = _controller.VictoryDocument;
        if (victoryDoc == null)
        {
            // Fallback: no result overlay available, just return to menu.
            SceneNav.Pop();
            return;
        }

        var result = gameObject.AddComponent<EndlessResultController>();
        result.Init(
            victoryDoc,
            _endless.ClearCount,
            _endless.LongestCombo,
            _endless.RunDurationSeconds
        );
    }
}
