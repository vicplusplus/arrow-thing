using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Endless mode: initial fill to ~70% occupancy (uniform-random heads +
/// uniform length sampling, like classic but capped at 70% instead of
/// saturation), then a continuous push-tick loop that spawns pending arrows
/// the player must clear before the next push or topout.
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

    private const float FrameBudgetMs = 12f;

    /// <summary>
    /// Target initial occupancy for endless mode: fills until ~70% of cells
    /// are occupied so the player has visible wiggle room to start.
    /// </summary>
    private const float InitialOccupancy = 0.70f;

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

        _controller.ShowLoadingInternal("Generating...");
        yield return null;

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

        yield return GenerateBoard(board, boardView);
        if (board.Arrows.Count == 0)
            yield break; // GenerateBoard logged + popped

        boardView.ApplyColoring();
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

    // ---- Initial board generation ------------------------------------------
    //
    // Mirrors ClassicMode.GenerateBoard but with one endless-specific flag:
    // ~70% occupancy cap (initial wiggle room). Centermost-first and
    // short-biased length sampling were both tried in the prototype but
    // dropped — uniform-random head selection + uniform length sampling
    // produce a more natural-feeling start state. Could be unified with
    // classic via a shared helper in a future pass; kept duplicated for
    // now to keep the endless reintroduction surgical.

    private IEnumerator GenerateBoard(Board board, BoardView boardView)
    {
        int targetCells = Mathf.RoundToInt(_w * _h * InitialOccupancy);
        var generator = BoardGeneration.FillBoardIncremental(
            board,
            _maxLen,
            _activeSeed,
            targetCellCount: targetCells
        );

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
                $"[EndlessMode] GenerateBoard produced 0 arrows (board {_w}x{_h}, maxLen={_maxLen}, seed={_activeSeed}). Returning to menu."
            );
            SceneNav.Pop();
            yield break;
        }
        Debug.Log(
            $"[EndlessMode] GenerateBoard complete: {board.Arrows.Count} arrows, board={_w}x{_h}, seed={_activeSeed}"
        );
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
