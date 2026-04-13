using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Top-level scene controller. Creates the board, spawns the view, and wires input.
/// </summary>
public sealed class GameController : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private VisualSettings visualSettings;

    [SerializeField]
    private Camera mainCamera;

    [SerializeField]
    private UIDocument victoryUIDocument;

    [SerializeField]
    private UIDocument hudUIDocument;

    [Header("Timer")]
    [Tooltip("Inspection phase duration in seconds.")]
    [SerializeField]
    private float inspectionDuration = 15f;

    [Tooltip("Inspection countdown turns red at this many seconds remaining.")]
    [SerializeField]
    private float inspectionWarningThreshold = 5f;

    [Header("Input")]
    [Tooltip("Screen-space distance in pixels before a click/tap becomes a drag instead of a tap.")]
    [SerializeField]
    private float dragThresholdPixels = 15f;

    [Header("Editor Overrides (ignored when launched from menu)")]
    [Tooltip(
        "Board width used when playing this scene directly. Ignored when coming from the main menu."
    )]
    [SerializeField]
    private int boardWidth = 6;

    [Tooltip(
        "Board height used when playing this scene directly. Ignored when coming from the main menu."
    )]
    [SerializeField]
    private int boardHeight = 6;

    [Tooltip(
        "Max arrow length used when playing this scene directly. Ignored when coming from the main menu."
    )]
    [SerializeField]
    private int maxArrowLength = 5;

    [Tooltip(
        "When checked, generates a random seed each run. When unchecked, uses the seed below. Only applies when playing this scene directly — menu always uses a random seed."
    )]
    [SerializeField]
    private bool useRandomSeed = true;

    [Tooltip(
        "Fixed seed for reproducible boards. Only used when 'Use Random Seed' is unchecked and playing this scene directly."
    )]
    [SerializeField]
    private int seed = 42;

    [Header("Loading Screen")]
    [Tooltip("Duration of the loading screen fade in/out in seconds.")]
    [SerializeField]
    private float loadingFadeDuration = 0.3f;

    // Game state
    private Board _board;
    private BoardView _boardView;
    private CameraController _camCtrl;
    private GameTimer _timer;
    private ReplayRecorder _recorder;
    private InputHandler _inputHandler;
    private string _gameId;
    private int _activeSeed;
    private int _w;
    private int _h;
    private int _maxLen;
    private float _inspectionDur;
    private int _initialArrowCount;
    private bool _autosaveEnabled;
    private bool _isContinuedGame;
    private int _clearsSinceLastSave;
    private const int AutosaveInterval = 10;
    private List<List<Cell>> _initialBoardSnapshot;
    private const float FrameBudgetMs = 12f;

    /// <summary>Set to true by the X button during loading to abort.</summary>
    private bool _cancelRequested;

    /// <summary>Set to true once the victory sequence begins. Blocks Escape/leave modal.</summary>
    private bool _victoryStarted;

    // Loading overlay state — driven by Update()
    private VisualElement _loadingOverlay;
    private VisualElement _loadingBarFill;
    private Label _loadingPercent;
    private Label _timerLabel;
    private Button _trailToggleBtn;
    private bool _trailOn;
    private Button _backBtn;
    private Button _retryBtn;
    private ConfirmModal _leaveModal;
    private ConfirmModal _retryModal;
    private FocusNavigator _focusNavigator;
    private VisualElement _cancelGenModal;
    private float _loadProgress;
    private bool _loadingActive;
    private float _loadingFadeStart;

    // --- Lifecycle ---

    private void Awake()
    {
        if (visualSettings == null)
        {
            Debug.LogError("GameController: VisualSettings is not assigned.");
            return;
        }
        if (mainCamera == null)
            mainCamera = Camera.main;
        if (mainCamera != null)
            mainCamera.backgroundColor = (ThemeManager.Current ?? visualSettings).backgroundColor;

        SettingsController.IsOpenChanged += OnSettingsOpenChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;

        // Create FocusNavigator early so navigation events are suppressed
        // from the start, even during generation/loading.
        if (hudUIDocument != null && hudUIDocument.rootVisualElement != null)
            _focusNavigator = new FocusNavigator(hudUIDocument.rootVisualElement);

        StartCoroutine(GenerateAndSetup());
    }

    private void OnDestroy()
    {
        _focusNavigator?.Dispose();
        SettingsController.IsOpenChanged -= OnSettingsOpenChanged;
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(VisualSettings theme)
    {
        if (mainCamera != null)
            mainCamera.backgroundColor = theme.backgroundColor;
        if (_boardView != null)
            _boardView.ApplyTheme(theme);
    }

    private void OnSettingsOpenChanged(bool open)
    {
        if (_inputHandler != null)
            _inputHandler.SetInputEnabled(!open);
    }

    private void Update()
    {
        // Tick FocusNavigator for modal keyboard nav (leave/cancel modals).
        if (_focusNavigator != null)
            _focusNavigator.Update();

        // Escape: open/close leave modal. Checked after FocusNavigator so
        // modal dismiss (via ConsumesCancel) runs first and this doesn't re-open it.
        // Blocked once the victory sequence starts — VictoryController owns input from that point.
        if (!_victoryStarted && NavigableScene.ShouldHandleCancel(_focusNavigator))
        {
            OnEscape();
        }

        if (!_loadingActive || _loadingOverlay == null)
            return;

        _loadingOverlay.style.opacity = Mathf.Clamp01(
            (Time.unscaledTime - _loadingFadeStart) / loadingFadeDuration
        );

        if (_loadingBarFill != null)
        {
            _loadingBarFill.style.width = new StyleLength(
                new Length(_loadProgress * 100f, LengthUnit.Percent)
            );
            if (_loadingPercent != null)
                _loadingPercent.text = Mathf.RoundToInt(_loadProgress * 100f) + "%";
        }
    }

    // --- Main setup orchestrator ---

    private IEnumerator GenerateAndSetup()
    {
        ResolveParameters(out ReplayData priorData, out bool deferredResume);
        ResolveHudElements();

        string modeStr =
            deferredResume ? "deferred-resume"
            : priorData != null ? "resume"
            : "new";
        Debug.Log(
            $"[GameController] GenerateAndSetup: mode={modeStr}, board={_w}x{_h}, maxLen={_maxLen}"
        );

        ShowLoading(deferredResume ? "Resuming..." : "Generating...");
        yield return null;

        if (deferredResume)
        {
            yield return LoadSaveAsync(result => priorData = result);
            if (priorData == null)
            {
                Debug.LogWarning(
                    "[GameController] Deferred save load returned null — returning to MainMenu."
                );
                SceneNav.Pop();
                yield break;
            }
            Debug.Log(
                $"[GameController] Save loaded: gameId={priorData.gameId}, board={priorData.boardWidth}x{priorData.boardHeight}, events={priorData.events.Count}"
            );
            ApplyResumeData(priorData);
        }

        ResolveSeed(priorData);
        bool hasSnapshot =
            priorData != null
            && priorData.boardSnapshot != null
            && priorData.boardSnapshot.Count > 0;

        CreateBoardAndView();
        SetupCamera();
        _boardView.SetCameraController(_camCtrl);

        if (hasSnapshot)
        {
            _initialBoardSnapshot = priorData.boardSnapshot;
            yield return RestoreBoard(priorData);
        }
        else
        {
            yield return GenerateBoard();
        }

        if (priorData != null)
        {
            bool resumeSolving = ReplayClears(priorData, out double resumeSolveElapsed);
            if (_board.Arrows.Count == 0)
            {
                SaveManager.Delete();
                SceneNav.Pop();
                yield break;
            }
            SetupResumedRecorder(priorData);
            FinalizeSession(priorData);
            _boardView.ApplyColoring();
            HideLoading();
            SetupTimer(resumeSolving, resumeSolveElapsed);
        }
        else
        {
            SetupNewRecorder();
            FinalizeSession(null);
            _boardView.ApplyColoring();
            HideLoading();
            SetupTimer(false, 0.0);
        }

        WireHud();
        WireInput();
        WireVictory();
    }

    // --- Parameter resolution ---

    private void ResolveParameters(out ReplayData priorData, out bool deferredResume)
    {
        _w = boardWidth;
        _h = boardHeight;
        _maxLen = maxArrowLength;
        _inspectionDur = inspectionDuration;
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

    private void ResolveHudElements()
    {
        if (hudUIDocument == null || hudUIDocument.rootVisualElement == null)
            return;

        var hudRoot = hudUIDocument.rootVisualElement;
        _loadingOverlay = hudRoot.Q("loading-overlay");
        _backBtn = hudRoot.Q<Button>("back-to-menu-btn");
        _retryBtn = hudRoot.Q<Button>("retry-btn");
        _timerLabel = hudRoot.Q<Label>("timer-label");
        _trailToggleBtn = hudRoot.Q<Button>("trail-toggle-btn");
        _cancelGenModal = hudRoot.Q("cancel-generation-modal");

        if (_loadingOverlay != null)
        {
            _loadingOverlay.style.display = DisplayStyle.None;
            _loadingOverlay.style.opacity = 0f;
            _loadingBarFill = _loadingOverlay.Q("loading-bar-fill");
            _loadingPercent = _loadingOverlay.Q<Label>("loading-percent");
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
            : (GameSettings.IsSet || useRandomSeed) ? Environment.TickCount
            : seed;
    }

    // --- Board and view creation ---

    private void CreateBoardAndView()
    {
        (_board, _boardView) = BoardSetupHelper.CreateBoardAndView(
            _w,
            _h,
            ThemeManager.Current ?? visualSettings
        );
    }

    private void SetupCamera()
    {
        if (mainCamera == null)
            return;
        float? zoom = GameSettings.IsSet
            ? PlayerPrefs.GetFloat(GameSettings.ZoomSpeedPrefKey, GameSettings.DefaultZoomSpeed)
            : null;
        _camCtrl = BoardSetupHelper.SetupCamera(mainCamera, _board, zoom);
    }

    // --- Work coroutines (no UI code — just work + _loadProgress) ---

    private IEnumerator RestoreBoard(ReplayData priorData)
    {
        int totalArrows = priorData.boardSnapshot.Count;
        Debug.Log($"[GameController] RestoreBoard: restoring {totalArrows} arrows from snapshot");
        int totalSteps = totalArrows * 2;
        var restorer = BoardSetupHelper.RestoreBoardFromSnapshot(
            _board,
            _boardView,
            priorData.boardSnapshot,
            FrameBudgetMs
        );

        while (restorer.MoveNext())
        {
            if (_cancelRequested)
            {
                SceneNav.Pop();
                yield break;
            }

            _loadProgress = (float)restorer.Current / totalSteps;
            yield return null;
        }
    }

    private IEnumerator GenerateBoard()
    {
        var generator = BoardGeneration.FillBoardIncremental(_board, _maxLen, _activeSeed);

        // Progress is split across four phases. Weights tuned from PlayMode
        // GenerationProfiler data (300x300), matching measured wall-time
        // fractions 70.8% / 8.5% / 13.0% / 7.7%:
        //   Generation    0% → 71%  (Burst domain + per-arrow view spawn)
        //   Compaction   71% → 79%
        //   RebuildViews 79% → 92%  (incremental, one view per iteration)
        //   Finalize     92% → 100%
        // Gen uses candidate depletion (1 - remaining/initial) with a ^0.7
        // exponent so the raw depletion (back-loaded in wall time) tracks
        // real time linearly. Rebuild is spread across multiple frames so the
        // bar keeps moving instead of stalling while views are recreated.
        const float genEndProgress = 0.71f;
        const float compactEndProgress = 0.79f;
        const float rebuildEndProgress = 0.92f;
        const float genProgressExponent = 0.7f;
        // Track Arrow refs so we can remove their views after compaction
        var spawnedArrows = new List<Arrow>();
        int arrowsBeforeCompaction = 0;
        float genFinalProgress = 0f;
        var phase = GenerationPhase.Generating;
        float compactStartRealtime = 0f;
        // Incremental view rebuild state (set at compact→finalize transition).
        // While rebuildingViews is true, the inner frame-budget loop adds
        // ArrowViews instead of advancing the generator.
        bool rebuildingViews = false;
        List<Arrow> rebuildList = null;
        int rebuildIndex = 0;
        int rebuildTotal = 0;

        while (true)
        {
            if (_cancelRequested)
            {
                // Defensive dispose in case the generator holds any IDisposable state.
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
                    // Spend remaining frame budget adding ArrowViews in batches.
                    if (rebuildIndex < rebuildTotal)
                    {
                        _boardView.AddArrowView(rebuildList[rebuildIndex]);
                        spawnedArrows.Add(rebuildList[rebuildIndex]);
                        rebuildIndex++;
                        continue;
                    }
                    // Rebuild complete; fall through to generator advance for finalize.
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
                        arrowsBeforeCompaction = _board.Arrows.Count;
                        // Snap gen progress to its full allocation at the moment of
                        // transition — all candidates have been consumed by now.
                        genFinalProgress = genEndProgress;
                        compactStartRealtime = Time.realtimeSinceStartup;
                    }
                    else if (
                        nextPhase == GenerationPhase.Finalizing
                        && phase == GenerationPhase.Compacting
                    )
                    {
                        // Compaction done — remove stale views synchronously (fast)
                        // and start incremental view rebuild that spans frames.
                        foreach (Arrow a in spawnedArrows)
                            _boardView.RemoveArrowView(a);
                        spawnedArrows.Clear();
                        rebuildList = new List<Arrow>(_board.Arrows);
                        rebuildIndex = 0;
                        rebuildTotal = rebuildList.Count;
                        rebuildingViews = true;
                    }
                    phase = nextPhase;
                }
                else if (phase == GenerationPhase.Generating)
                {
                    Arrow arrow = _board.Arrows[spawnedArrows.Count];
                    _boardView.AddArrowView(arrow);
                    spawnedArrows.Add(arrow);
                }
            }

            // Progress calculation per phase
            if (rebuildingViews)
            {
                float rebuildRatio = rebuildTotal > 0 ? (float)rebuildIndex / rebuildTotal : 1f;
                _loadProgress =
                    compactEndProgress + (rebuildEndProgress - compactEndProgress) * rebuildRatio;
            }
            else
            {
                switch (phase)
                {
                    case GenerationPhase.Generating:
                    {
                        int initial = _board.InitialCandidateCount;
                        int remaining = _board.RemainingCandidateCount;
                        float depletion = initial > 0 ? 1f - (float)remaining / initial : 0f;
                        _loadProgress =
                            genEndProgress
                            * Mathf.Pow(Mathf.Clamp01(depletion), genProgressExponent);
                        break;
                    }
                    case GenerationPhase.Compacting:
                    {
                        // Merge-based signal: peaks early because the final "no-merge
                        // verification pass" produces no new merges, and observed merge
                        // ratios are ~0.12–0.15 of arrow count.
                        int mergesCompleted = arrowsBeforeCompaction - _board.Arrows.Count;
                        float mergeRatio =
                            arrowsBeforeCompaction > 0
                                ? Mathf.Clamp01(
                                    (float)mergesCompleted / (arrowsBeforeCompaction * 0.12f)
                                )
                                : 1f;
                        // Wall-time fallback: linear over expected compact duration.
                        // Fit from ProfilePhaseTimings data: compact_ms ≈ 5e-5 * arrows^1.78.
                        // Ensures the bar keeps moving during the no-merge final pass.
                        float expectedCompactSec =
                            5e-5f * Mathf.Pow(arrowsBeforeCompaction, 1.78f) / 1000f;
                        float elapsed = Time.realtimeSinceStartup - compactStartRealtime;
                        float timeRatio =
                            expectedCompactSec > 0.001f
                                ? Mathf.Clamp01(elapsed / expectedCompactSec)
                                : 1f;
                        float ratio = Mathf.Max(mergeRatio, timeRatio);
                        _loadProgress =
                            genFinalProgress + (compactEndProgress - genFinalProgress) * ratio;
                        break;
                    }
                    case GenerationPhase.Finalizing:
                    {
                        int arrowCount = _board.Arrows.Count;
                        float finalizeRatio =
                            arrowCount > 0 && generator.Current is int finalized
                                ? Mathf.Clamp01((float)finalized / arrowCount)
                                : 0f;
                        _loadProgress =
                            rebuildEndProgress + (1f - rebuildEndProgress) * finalizeRatio;
                        break;
                    }
                }
            }

            if (done)
                break;
            yield return null;
        }

        if (_board.Arrows.Count == 0)
        {
            Debug.LogWarning(
                $"[GameController] GenerateBoard produced 0 arrows (board {_w}x{_h}, maxLen={_maxLen}, seed={_activeSeed}). Returning to menu."
            );
            SceneNav.Pop();
            yield break;
        }
        Debug.Log(
            $"[GameController] GenerateBoard complete: {_board.Arrows.Count} arrows, board={_w}x{_h}, seed={_activeSeed}"
        );

        _initialBoardSnapshot = new List<List<Cell>>(_board.Arrows.Count);
        foreach (Arrow arrow in _board.Arrows)
            _initialBoardSnapshot.Add(new List<Cell>(arrow.Cells));
    }

    // --- Resume logic ---

    private bool ReplayClears(ReplayData priorData, out double solveElapsed)
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
                Cell cell = BoardCoords.WorldToCell(worldPos, _board.Width, _board.Height);
                if (_board.Contains(cell))
                {
                    Arrow arrow = _board.GetArrowAt(cell);
                    if (arrow != null && _board.IsClearable(arrow))
                    {
                        _boardView.RemoveArrowView(arrow);
                        _board.RemoveArrow(arrow);
                        successfulClears++;
                    }
                    else if (arrow == null)
                        Debug.LogWarning(
                            $"[GameController] ReplayClears: no arrow at cell ({cell.X},{cell.Y}) for clear event seq={evt.seq}"
                        );
                    else
                        Debug.LogWarning(
                            $"[GameController] ReplayClears: arrow at ({cell.X},{cell.Y}) not clearable for clear event seq={evt.seq}"
                        );
                }
                else
                {
                    Debug.LogWarning(
                        $"[GameController] ReplayClears: clear event seq={evt.seq} maps to out-of-bounds cell ({cell.X},{cell.Y})"
                    );
                }
            }
            if (evt.type == ReplayEventType.StartSolve)
                solving = true;
        }
        solveElapsed = priorData.ComputedSolveElapsed;
        Debug.Log(
            $"[GameController] ReplayClears: {successfulClears}/{totalClearEvents} clears applied, solving={solving}, solveElapsed={solveElapsed:F3}s"
        );
        return solving;
    }

    private void SetupResumedRecorder(ReplayData priorData)
    {
        _gameId = priorData.gameId;
        int nextSeq =
            priorData.events.Count > 0 ? priorData.events[priorData.events.Count - 1].seq + 1 : 0;
        _recorder = new ReplayRecorder(priorData.events, nextSeq);
        _recorder.RecordSessionRejoin();
        Debug.Log(
            $"[GameController] Resumed game: id={_gameId}, nextSeq={nextSeq}, remainingArrows={_board.Arrows.Count}"
        );
    }

    private void SetupNewRecorder()
    {
        _gameId = Guid.NewGuid().ToString();
        _recorder = new ReplayRecorder();
        _recorder.RecordSessionStart();
        Debug.Log(
            $"[GameController] New game: id={_gameId}, arrows={_board.Arrows.Count}, board={_w}x{_h}, seed={_activeSeed}"
        );
    }

    private void FinalizeSession(ReplayData priorData)
    {
        _initialArrowCount = _board.Arrows.Count;
        _autosaveEnabled = !SaveManager.HasSave() || priorData != null;
        _isContinuedGame = priorData != null;
    }

    // --- Timer and gameplay wiring ---

    private void SetupTimer(bool resumeSolving, double resumeSolveElapsed)
    {
        _timer = new GameTimer(_inspectionDur);
        double wallNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (resumeSolving)
        {
            _timer.Resume(wallNow, resumeSolveElapsed);
            Debug.Log($"[GameController] Timer resumed: priorElapsed={resumeSolveElapsed:F3}s");
        }
        else
        {
            _timer.Start(wallNow);
            Debug.Log(
                $"[GameController] Timer started fresh: inspectionDuration={_inspectionDur}s"
            );
        }
    }

    private void WireHud()
    {
        if (hudUIDocument == null || hudUIDocument.rootVisualElement == null)
            return;

        var hudRoot = hudUIDocument.rootVisualElement;

        // Single leave modal, reconfigured per ShowLeave based on save state.
        _leaveModal = new ConfirmModal(hudRoot.Q("leave-modal"), "Leave?", "Leave", "Stay");
        _leaveModal.Confirmed += OnLeaveConfirm;
        _leaveModal.Cancelled += OnLeaveCancel;
        _leaveModal.Dismissed += OnLeaveDismiss;

        _retryModal = new ConfirmModal(hudRoot.Q("retry-modal"), "Retry?", "Retry", "Cancel");
        _retryModal.Confirmed += OnRetryConfirm;
        _retryModal.Cancelled += OnRetryCancel;

        if (_backBtn != null)
        {
            _backBtn.clickable = new Clickable(() => { });
            _backBtn.clicked += ShowLeave;
        }

        if (_retryBtn != null)
        {
            _retryBtn.clickable = new Clickable(() => { });
            _retryBtn.clicked += OnRetryClicked;
        }

        var timerView = gameObject.AddComponent<GameTimerView>();
        timerView.Init(_timer, hudUIDocument, inspectionWarningThreshold);

        if (_trailToggleBtn != null)
        {
            _trailToggleBtn.clicked += ToggleTrail;
            _boardView.TrailAutoOff += () =>
            {
                _trailOn = false;
                _trailToggleBtn.RemoveFromClassList("hud-icon-btn--active");
            };
        }

        // Add HUD buttons to FocusNavigator for keyboard accessibility.
        if (_focusNavigator != null)
        {
            var items = new System.Collections.Generic.List<FocusNavigator.FocusItem>();
            int backIdx = -1;
            int retryIdx = -1;
            int trailIdx = -1;

            if (_backBtn != null)
            {
                backIdx = items.Count;
                items.Add(
                    new FocusNavigator.FocusItem
                    {
                        Element = _backBtn,
                        OnActivate = () =>
                        {
                            ShowLeave();
                            return true;
                        },
                    }
                );
            }
            if (_retryBtn != null)
            {
                retryIdx = items.Count;
                items.Add(
                    new FocusNavigator.FocusItem
                    {
                        Element = _retryBtn,
                        OnActivate = () =>
                        {
                            OnRetryClicked();
                            return true;
                        },
                    }
                );
            }
            if (_trailToggleBtn != null)
            {
                trailIdx = items.Count;
                items.Add(
                    new FocusNavigator.FocusItem
                    {
                        Element = _trailToggleBtn,
                        OnActivate = () =>
                        {
                            ToggleTrail();
                            return true;
                        },
                    }
                );
            }

            if (items.Count > 0)
            {
                _focusNavigator.SetItems(items);
                // back (top-left) ↔ Right ↔ retry (top-right)
                if (backIdx >= 0 && retryIdx >= 0)
                    _focusNavigator.LinkBidi(backIdx, FocusNavigator.NavDir.Right, retryIdx);
                // retry (top-right) ↔ Down ↔ trail (bottom-right)
                if (retryIdx >= 0 && trailIdx >= 0)
                    _focusNavigator.LinkBidi(retryIdx, FocusNavigator.NavDir.Down, trailIdx);
                // back (top-left) ↔ Down ↔ trail (bottom-right)
                if (backIdx >= 0 && trailIdx >= 0)
                    _focusNavigator.LinkBidi(backIdx, FocusNavigator.NavDir.Down, trailIdx);
            }
        }
    }

    private void WireInput()
    {
        float dragThreshold = GameSettings.IsSet
            ? PlayerPrefs.GetFloat(
                GameSettings.DragThresholdPrefKey,
                GameSettings.DefaultDragThreshold
            )
            : dragThresholdPixels;
        _inputHandler = gameObject.AddComponent<InputHandler>();
        _inputHandler.Init(
            _board,
            _boardView,
            _camCtrl,
            dragThreshold,
            _timer,
            _recorder,
            OnArrowCleared,
            onQuickReset: OnQuickReset,
            onQuickSave: OnQuickSave,
            onToggleTrail: ToggleTrail
        );

        // Apply keep-trail setting from PlayerPrefs.
        _boardView.KeepTrailAfterClear = PlayerPrefs.GetInt(GameSettings.KeepTrailPrefKey, 0) == 1;

        if (KeybindManager.Instance != null)
            KeybindManager.Instance.ActiveContext = KeybindManager.Context.Gameplay;

        if (_backBtn != null)
            _backBtn.clicked += () => _inputHandler.SetInputEnabled(false);
    }

    private void OnQuickReset()
    {
        SceneNav.Replace("Game");
    }

    private void OnRetryClicked()
    {
        if (HasAnyClearedArrows)
        {
            _retryModal?.Show();
            if (_inputHandler != null)
                _inputHandler.SetInputEnabled(false);
        }
        else
        {
            OnQuickReset();
        }
    }

    private void OnRetryConfirm()
    {
        _retryModal?.Hide();
        OnQuickReset();
    }

    private void OnRetryCancel()
    {
        _retryModal?.Hide();
        if (_inputHandler != null)
            _inputHandler.SetInputEnabled(true);
    }

    private void OnQuickSave()
    {
        if (_recorder != null)
            SaveManager.Save(BuildReplayData());
    }

    private void OnEscape()
    {
        if (_leaveModal != null && _leaveModal.IsVisible)
        {
            OnLeaveDismiss();
            return;
        }
        ShowLeave();
    }

    private void ShowLeave()
    {
        if (_leaveModal == null)
            return;

        if (WouldOverwriteDifferentSave)
        {
            _leaveModal.Reconfigure(
                "Save before leaving?",
                "Save & Leave",
                "Leave without saving",
                subtitle: "This will replace your current save.",
                isDismissable: true
            );
        }
        else
        {
            _leaveModal.Reconfigure("Leave?", "Leave", "Stay");
        }

        _leaveModal.Show();
        if (_inputHandler != null)
            _inputHandler.SetInputEnabled(false);
    }

    private void OnLeaveConfirm()
    {
        if (WouldOverwriteDifferentSave)
            SaveAndLeave();
        else if (_autosaveEnabled && (_isContinuedGame || HasAnyClearedArrows))
            SaveAndLeave();
        else
            ReturnToModeSelect();
    }

    private void OnLeaveCancel()
    {
        if (WouldOverwriteDifferentSave)
            ReturnToModeSelect(); // "Leave without saving"
        else
            OnLeaveDismiss(); // "Stay"
    }

    private void ToggleTrail()
    {
        _trailOn = !_trailOn;
        _boardView.SetAllTrailsVisible(_trailOn);
        if (_trailToggleBtn != null)
        {
            if (_trailOn)
                _trailToggleBtn.AddToClassList("hud-icon-btn--active");
            else
                _trailToggleBtn.RemoveFromClassList("hud-icon-btn--active");
        }
    }

    private void WireVictory()
    {
        if (
            victoryUIDocument == null
            || !victoryUIDocument.enabled
            || victoryUIDocument.rootVisualElement == null
        )
            return;

        var victory = gameObject.AddComponent<VictoryController>();
        victory.Init(
            victoryUIDocument,
            _boardView.GridRenderer,
            _camCtrl,
            _w,
            _h,
            _timer,
            hudUIDocument,
            BuildReplayData
        );
        _boardView.LastArrowClearing += () =>
        {
            _victoryStarted = true;
            _inputHandler.SetInputEnabled(false);
            if (_backBtn != null)
                _backBtn.style.display = DisplayStyle.None;
            if (_retryBtn != null)
                _retryBtn.style.display = DisplayStyle.None;
            if (_recorder != null)
                _recorder.RecordEndSolve();
            if (_autosaveEnabled)
                SaveManager.Delete();
            victory.OnLastArrowClearing();
        };
        _boardView.BoardCleared += victory.OnBoardCleared;
    }

    // --- Loading overlay ---

    private void ShowLoading(string label)
    {
        if (_loadingOverlay == null)
            return;
        var loadingLabel = _loadingOverlay.Q<Label>("loading-label");
        if (loadingLabel != null)
            loadingLabel.text = label;
        if (_timerLabel != null)
            _timerLabel.style.display = DisplayStyle.None;
        if (_trailToggleBtn != null)
            _trailToggleBtn.style.display = DisplayStyle.None;
        if (_retryBtn != null)
            _retryBtn.style.display = DisplayStyle.None;
        if (_backBtn != null && _cancelGenModal != null)
        {
            _backBtn.clicked += () => _cancelGenModal.RemoveFromClassList("modal--hidden");
            _cancelGenModal.Q<Button>("cancel-generation-yes-btn").clicked += () =>
            {
                _cancelGenModal.AddToClassList("modal--hidden");
                _cancelRequested = true;
            };
            _cancelGenModal.Q<Button>("cancel-generation-no-btn").clicked += () =>
                _cancelGenModal.AddToClassList("modal--hidden");
        }
        else if (_backBtn != null)
        {
            _backBtn.clicked += () => _cancelRequested = true;
        }

        _loadingOverlay.style.display = DisplayStyle.Flex;
        _loadingOverlay.style.opacity = 0f;
        _loadProgress = 0f;
        _loadingActive = true;
        _loadingFadeStart = Time.unscaledTime;
    }

    private void HideLoading()
    {
        _loadingActive = false;
        if (_loadingOverlay != null)
        {
            float currentOpacity = Mathf.Clamp01(
                (Time.unscaledTime - _loadingFadeStart) / loadingFadeDuration
            );
            StartCoroutine(
                FadeElement(
                    _loadingOverlay,
                    currentOpacity,
                    0f,
                    loadingFadeDuration * currentOpacity,
                    hide: true
                )
            );
        }
        if (_timerLabel != null)
            _timerLabel.style.display = DisplayStyle.Flex;
        if (_trailToggleBtn != null)
            _trailToggleBtn.style.display = DisplayStyle.Flex;
        if (_retryBtn != null)
            _retryBtn.style.display = DisplayStyle.Flex;
        if (_backBtn != null)
            _backBtn.clickable = new Clickable(() => { });
        if (_cancelGenModal != null)
            _cancelGenModal.AddToClassList("modal--hidden");
    }

    // --- Leave modal ---

    private bool HasAnyClearedArrows => _board != null && _board.Arrows.Count < _initialArrowCount;

    private bool WouldOverwriteDifferentSave =>
        !_autosaveEnabled && HasAnyClearedArrows && SaveManager.HasSave();

    private void OnLeaveDismiss()
    {
        _leaveModal?.Hide();
        if (_inputHandler != null)
            _inputHandler.SetInputEnabled(true);
    }

    // --- Save ---

    private void OnArrowCleared()
    {
        if (!_autosaveEnabled || _recorder == null || _timer == null)
            return;

        _clearsSinceLastSave++;
        if (_clearsSinceLastSave >= AutosaveInterval)
        {
            _clearsSinceLastSave = 0;
            int cleared = _initialArrowCount - _board.Arrows.Count;
            Debug.Log(
                $"[GameController] Autosave triggered: {cleared}/{_initialArrowCount} arrows cleared"
            );
            SaveManager.Save(BuildReplayData());
        }
    }

    private static void ReturnToModeSelect()
    {
        SceneNav.Pop();
    }

    private void SaveAndLeave()
    {
        if (_recorder != null && _timer != null)
        {
            _recorder.RecordSessionLeave();
            Debug.Log(
                $"[GameController] SaveAndLeave: saving game id={_gameId}, arrows remaining={_board?.Arrows.Count}"
            );
            SaveManager.Save(BuildReplayData());
        }
        ReturnToModeSelect();
    }

    private ReplayData BuildReplayData()
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

    // --- Utilities ---

    private static IEnumerator FadeElement(
        VisualElement element,
        float from,
        float to,
        float duration,
        bool hide = false
    )
    {
        float start = Time.unscaledTime;
        while (true)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - start) / duration);
            element.style.opacity = Mathf.Lerp(from, to, t);
            if (t >= 1f)
                break;
            yield return null;
        }
        if (hide)
            element.style.display = DisplayStyle.None;
    }
}
