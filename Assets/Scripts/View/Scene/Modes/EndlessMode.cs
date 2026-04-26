using System;
using System.Collections;
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

    // Resolved board parameters (same shape as ClassicMode but no timer/recorder/save).
    private int _w;
    private int _h;
    private int _maxLen;
    private int _activeSeed;

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

    public void OnTapResult(TapResult result)
    {
        // Endless routes real-arrow clears through EndlessBoardSession via
        // BoardView.SetArrowRemover (wired in Setup). Stats + immediate-mode
        // shortfall placement happen inside EndlessModeController.HandleRealArrowCleared.
        // No replay recording, no save autosave, no inspection-phase timer.
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

        Debug.Log($"[EndlessMode] Setup: board={_w}x{_h}, maxLen={_maxLen}, seed={_activeSeed}");

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
            _maxLen,
            spawnSeed: _activeSeed,
            hudRoot: hudRoot,
            camera: context.MainCamera
        );

        // Route real-arrow clears through the session so its NativeGenerationState
        // (used for ghost cycle detection) sees the cleared arrow vanish.
        boardView.SetArrowRemover(_endless.HandleRealArrowCleared);

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
            _w = GameSettings.Width;
            _h = GameSettings.Height;
            _maxLen = GameSettings.MaxArrowLength;
        }
        else
        {
            _w = _controller.EditorBoardWidth;
            _h = _controller.EditorBoardHeight;
            _maxLen = _controller.EditorMaxArrowLength;
        }
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

        // Freeze gameplay: input off, HUD buttons hidden. Player sees the
        // saturated board with the danger tint locked at full red until the
        // result screen appears.
        _controller.DisableGameplayHudAndInput();
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
