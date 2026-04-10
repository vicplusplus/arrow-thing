using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UIElements;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

/// <summary>
/// Scene controller for the replay viewer. Restores a board from snapshot,
/// plays back clear/reject events via <see cref="ReplayPlayer"/>, and
/// provides seek, speed, play/pause, and clearable highlighting controls.
/// </summary>
public sealed class ReplayViewController : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private VisualSettings visualSettings;

    [SerializeField]
    private Camera mainCamera;

    [SerializeField]
    private UIDocument hudUIDocument;

    [Header("Tap Indicator")]
    [SerializeField]
    private Sprite tapRingSprite;

    [SerializeField]
    private float tapIndicatorDuration = 0.4f;

    [SerializeField]
    private float tapIndicatorMaxScale = 1.5f;

    private const float FrameBudgetMs = 12f;

    // Domain
    private Board _board;
    private BoardView _boardView;
    private CameraController _camCtrl;
    private ReplayPlayer _player;
    private ReplayData _replayData;

    // UI elements
    private VisualElement _loadingOverlay;
    private VisualElement _loadingBarFill;
    private Label _loadingPercent;
    private Label _timeCurrent;
    private Label _timeTotal;
    private VisualElement _seekTrack;
    private VisualElement _seekFill;
    private VisualElement _seekHandle;
    private Button _playPauseBtn;
    private VisualElement _playPauseIcon;
    private Button _speedBtn;
    private Button _exitBtn;
    private Button _highlightBtn;
    private VisualElement _controlsBar;
    private Button _controlsToggleBtn;
    private VisualElement _controlsToggleIcon;

    // State
    private bool _highlightActive;
    private bool _controlsVisible = true;
    private bool _isSeeking;
    private bool _wasPlayingBeforeSeek;
    private TapIndicatorPool _tapPool;
    private List<List<Cell>> _boardSnapshot;

    // Camera input
    private InputAction _pointAction;
    private InputAction _selectAction;
    private InputAction _zoomAction;
    private Vector2 _pressStartScreen;
    private Vector3 _pressStartWorld;
    private bool _isPressed;
    private bool _isDragging;
    private float _dragThreshold = 15f;
    private bool _inputEnabled = true;
    private bool _pointerOverUI;

    // Keyboard navigation
    private FocusNavigator _replayNavigator;
    private int _navPlayIdx;
    private int _navSpeedIdx;
    private int _navHighlightIdx;
    private int _navSeekIdx;
    private int _navExitIdx;

    private void Awake()
    {
        if (visualSettings == null)
        {
            Debug.LogError("ReplayViewController: VisualSettings is not assigned.");
            return;
        }

        _replayData = GameSettings.ReplaySource;
        if (_replayData == null)
        {
            Debug.LogError("ReplayViewController: No replay data in GameSettings.");
            SceneNav.Pop();
            return;
        }

        if (mainCamera == null)
            mainCamera = Camera.main;
        if (mainCamera != null)
            mainCamera.backgroundColor = (ThemeManager.Current ?? visualSettings).backgroundColor;

        SettingsController.IsOpenChanged += OnSettingsOpenChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;

        // Create navigator early so navigation events are suppressed from the start.
        if (hudUIDocument != null && hudUIDocument.rootVisualElement != null)
            new FocusNavigator(hudUIDocument.rootVisualElement);

        if (KeybindManager.Instance != null)
            KeybindManager.Instance.ActiveContext = KeybindManager.Context.Replay;

        StartCoroutine(LoadAndPlay());
    }

    private void OnDestroy()
    {
        SettingsController.IsOpenChanged -= OnSettingsOpenChanged;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        if (EnhancedTouchSupport.enabled)
            EnhancedTouchSupport.Disable();
    }

    private void OnThemeChanged(VisualSettings theme)
    {
        if (mainCamera != null)
            mainCamera.backgroundColor = theme.backgroundColor;
        if (_boardView != null)
            _boardView.ApplyTheme(theme);
        if (_tapPool != null)
        {
            var (clearColor, rejectColor) = TapColorsForTheme(theme);
            _tapPool.UpdateColors(clearColor, rejectColor);
        }
    }

    private static (Color clear, Color reject) TapColorsForTheme(VisualSettings theme)
    {
        bool lightBg = theme.backgroundColor.grayscale > 0.5f;
        Color clear = lightBg ? new Color(0.1f, 0.1f, 0.15f, 0.8f) : new Color(1f, 1f, 1f, 0.8f);
        Color reject = lightBg
            ? new Color(0.75f, 0.15f, 0.15f, 0.8f)
            : new Color(1f, 0.3f, 0.3f, 0.8f);
        return (clear, reject);
    }

    private void OnSettingsOpenChanged(bool open)
    {
        _inputEnabled = !open;
        if (open && _player != null && _player.IsPlaying)
        {
            _player.IsPlaying = false;
            UpdatePlayPauseButton();
        }
    }

    private IEnumerator LoadAndPlay()
    {
        ResolveHudElements();
        ShowLoading();
        yield return null;

        // Create board and view
        (_board, _boardView) = BoardSetupHelper.CreateBoardAndView(
            _replayData.boardWidth,
            _replayData.boardHeight,
            ThemeManager.Current ?? visualSettings
        );

        _camCtrl = BoardSetupHelper.SetupCamera(mainCamera, _board);

        // Restore board from snapshot, or regenerate from seed if snapshot is absent
        _boardSnapshot = _replayData.boardSnapshot;
        if (_boardSnapshot == null || _boardSnapshot.Count == 0)
        {
            Debug.Log(
                $"[ReplayViewController] No snapshot — regenerating board from seed={_replayData.seed}"
            );

            var rng = new System.Random(_replayData.seed);
            var gen = BoardGeneration.FillBoardIncremental(_board, _replayData.maxArrowLength, rng);

            while (gen.MoveNext())
            {
                if (gen.Current is GenerationPhase)
                    continue;
                yield return null;
            }

            // Build snapshot from the generated board for seek support (backward seek)
            _boardSnapshot = new List<List<Cell>>();
            foreach (var arrow in _board.Arrows)
                _boardSnapshot.Add(new List<Cell>(arrow.Cells));

            // Persist the replay with snapshot locally so subsequent views load instantly
            _replayData.boardSnapshot = _boardSnapshot;
            var lbManager = LeaderboardManager.Instance;
            if (lbManager != null)
            {
                lbManager.RecordResult(_replayData);
                Debug.Log(
                    $"[ReplayViewController] Saved replay with snapshot locally: {_replayData.gameId}"
                );
            }

            // Spawn arrow views
            foreach (var arrow in _board.Arrows)
                _boardView.AddArrowView(arrow);
        }
        else
        {
            int totalArrows = _boardSnapshot.Count;
            int totalSteps = totalArrows * 2;
            var restorer = BoardSetupHelper.RestoreBoardFromSnapshot(
                _board,
                _boardView,
                _boardSnapshot,
                FrameBudgetMs
            );

            while (restorer.MoveNext())
            {
                float progress = (float)restorer.Current / totalSteps;
                UpdateLoadingProgress(progress);
                yield return null;
            }
        }

        _boardView.ApplyColoring();
        HideLoading();

        Debug.Log(
            $"[ReplayViewController] Replay loaded: board={_replayData.boardWidth}x{_replayData.boardHeight}, arrows={_board.Arrows.Count}, events={_replayData.events.Count}"
        );

        // Create replay player
        _player = new ReplayPlayer(_replayData);

        // Set up tap indicator pool — use dark rings on light backgrounds
        var (clearColor, rejectColor) = TapColorsForTheme(ThemeManager.Current ?? visualSettings);
        _tapPool = new TapIndicatorPool(
            tapRingSprite,
            tapIndicatorDuration,
            tapIndicatorMaxScale,
            transform,
            clearColor,
            rejectColor
        );

        // Wire input for camera pan/zoom
        SetupCameraInput();

        // Wire HUD
        WireHud();

        // Consume replay settings
        GameSettings.ClearReplay();
    }

    private void Update()
    {
        if (_player == null)
            return;

        // Camera input (suppress while interacting with UI)
        if (_inputEnabled && !_isSeeking && !_pointerOverUI)
            HandleCameraInput();

        // Advance playback (skip while seeking)
        if (!_isSeeking && _player.IsPlaying && !_player.IsFinished)
        {
            var fired = _player.Advance(Time.deltaTime);
            foreach (var evt in fired)
                ExecuteEvent(evt);
        }

        // Update UI
        UpdateSeekUI();
        UpdateTimeLabels();

        // Auto-pause when finished
        if (_player.IsFinished && _player.IsPlaying)
        {
            _player.IsPlaying = false;
            UpdatePlayPauseButton();
        }

        HandleReplayKeybinds();
    }

    private void HandleReplayKeybinds()
    {
        var km = KeybindManager.Instance;
        if (km == null)
            return;
        if (SettingsController.Instance != null && SettingsController.Instance.IsOpen)
            return;

        // Navigator handles arrow keys, Enter, Tab.
        if (_replayNavigator != null)
            _replayNavigator.Update();

        // Space always toggles play/pause regardless of focus.
        if (
            UnityEngine.InputSystem.Keyboard.current != null
            && UnityEngine.InputSystem.Keyboard.current.spaceKey.wasPressedThisFrame
        )
            OnPlayPause();

        // T toggles clearable highlight.
        if (
            UnityEngine.InputSystem.Keyboard.current != null
            && UnityEngine.InputSystem.Keyboard.current.tKey.wasPressedThisFrame
        )
            OnHighlightToggle();

        // Escape exits (unless a modal is handling it).
        if (NavigableScene.ShouldHandleCancel(_replayNavigator))
            OnExit();
    }

    // --- HUD ---

    private void ResolveHudElements()
    {
        if (hudUIDocument == null || hudUIDocument.rootVisualElement == null)
            return;

        var root = hudUIDocument.rootVisualElement;
        _loadingOverlay = root.Q("loading-overlay");
        _loadingBarFill = root.Q("loading-bar-fill");
        _loadingPercent = root.Q<Label>("loading-percent");
        _timeCurrent = root.Q<Label>("time-current");
        _timeTotal = root.Q<Label>("time-total");
        _seekTrack = root.Q("seek-track");
        _seekFill = root.Q("seek-fill");
        _seekHandle = root.Q("seek-handle");
        _playPauseBtn = root.Q<Button>("play-pause-btn");
        _playPauseIcon = root.Q("play-pause-icon");
        _speedBtn = root.Q<Button>("speed-btn");
        _exitBtn = root.Q<Button>("exit-btn");
        _highlightBtn = root.Q<Button>("highlight-btn");
        _controlsBar = root.Q("controls-bar");
        _controlsToggleBtn = root.Q<Button>("controls-toggle-btn");
        _controlsToggleIcon = root.Q("controls-toggle-icon");

        if (_controlsBar != null)
            _controlsBar.style.display = DisplayStyle.None;
        if (_controlsToggleBtn != null)
            _controlsToggleBtn.style.display = DisplayStyle.None;
    }

    private void WireHud()
    {
        if (_controlsBar != null)
            _controlsBar.style.display = DisplayStyle.Flex;
        if (_controlsToggleBtn != null)
        {
            _controlsToggleBtn.style.display = DisplayStyle.Flex;
            _controlsToggleBtn.clicked += OnControlsToggle;
        }

        if (_exitBtn != null)
            _exitBtn.clicked += OnExit;

        if (_playPauseBtn != null)
            _playPauseBtn.clicked += OnPlayPause;

        if (_speedBtn != null)
            _speedBtn.clicked += OnSpeedCycle;

        if (_highlightBtn != null)
            _highlightBtn.clicked += OnHighlightToggle;

        if (_seekTrack != null)
        {
            _seekTrack.RegisterCallback<PointerDownEvent>(OnSeekPointerDown);
            _seekTrack.RegisterCallback<PointerMoveEvent>(OnSeekPointerMove);
            _seekTrack.RegisterCallback<PointerUpEvent>(OnSeekPointerUp);
            _seekTrack.RegisterCallback<PointerCaptureOutEvent>(OnSeekCaptureOut);
        }

        // Suppress camera pan when interacting with any HUD element
        if (_controlsBar != null)
        {
            _controlsBar.RegisterCallback<PointerDownEvent>(_ => _pointerOverUI = true);
            _controlsBar.RegisterCallback<PointerUpEvent>(_ => _pointerOverUI = false);
        }
        if (_controlsToggleBtn != null)
        {
            _controlsToggleBtn.RegisterCallback<PointerDownEvent>(_ => _pointerOverUI = true);
            _controlsToggleBtn.RegisterCallback<PointerUpEvent>(_ => _pointerOverUI = false);
        }
        if (_exitBtn != null)
        {
            _exitBtn.RegisterCallback<PointerDownEvent>(_ => _pointerOverUI = true);
            _exitBtn.RegisterCallback<PointerUpEvent>(_ => _pointerOverUI = false);
        }

        if (_timeTotal != null)
            _timeTotal.text = FormatTime(_player.DisplayDuration);

        UpdatePlayPauseButton();
        BuildReplayNavigator();
    }

    private void BuildReplayNavigator()
    {
        var root = hudUIDocument.rootVisualElement;
        _replayNavigator?.Dispose();
        _replayNavigator = new FocusNavigator(root);

        var items = new System.Collections.Generic.List<FocusNavigator.FocusItem>();

        // Exit button.
        _navExitIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _exitBtn,
                OnActivate = () =>
                {
                    OnExit();
                    return true;
                },
            }
        );

        // Seek handle — OnHorizontal is already DAS-gated by the navigator.
        _navSeekIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _seekHandle,
                OnHorizontal = dir =>
                {
                    if (_player != null)
                    {
                        float duration = (float)_player.DisplayDuration;
                        float step = duration * 0.02f; // 2% per step
                        float target = (float)_player.CurrentTime + dir * step;
                        target = Mathf.Clamp(target, 0f, duration);
                        SeekTo(target);
                    }
                    return true;
                },
            }
        );

        // Play/Pause button.
        _navPlayIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _playPauseBtn,
                OnActivate = () =>
                {
                    OnPlayPause();
                    return true;
                },
            }
        );

        // Speed button.
        _navSpeedIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _speedBtn,
                OnActivate = () =>
                {
                    OnSpeedCycle();
                    return true;
                },
            }
        );

        // Highlight/Clearable button.
        _navHighlightIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _highlightBtn,
                OnActivate = () =>
                {
                    OnHighlightToggle();
                    return true;
                },
            }
        );

        // Controls toggle (show/hide bar).
        int toggleIdx = -1;
        if (_controlsToggleBtn != null)
        {
            toggleIdx = items.Count;
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = _controlsToggleBtn,
                    OnActivate = () =>
                    {
                        OnControlsToggle();
                        return true;
                    },
                }
            );
        }

        _replayNavigator.SetItems(items, _navPlayIdx);

        // Nav graph:
        // Exit ↔ Seek (Down/Up)
        _replayNavigator.LinkBidi(_navExitIdx, FocusNavigator.NavDir.Down, _navSeekIdx);
        // Seek ↔ Play (Down/Up)
        _replayNavigator.LinkBidi(_navSeekIdx, FocusNavigator.NavDir.Down, _navPlayIdx);
        // Play ↔ Speed ↔ Highlight (Right chain)
        _replayNavigator.LinkBidi(_navPlayIdx, FocusNavigator.NavDir.Right, _navSpeedIdx);
        _replayNavigator.LinkBidi(_navSpeedIdx, FocusNavigator.NavDir.Right, _navHighlightIdx);
        // Highlight ↔ Controls toggle (Right)
        if (toggleIdx >= 0)
            _replayNavigator.LinkBidi(_navHighlightIdx, FocusNavigator.NavDir.Right, toggleIdx);
        // All playback row buttons → Up → Seek
        _replayNavigator.Link(_navPlayIdx, FocusNavigator.NavDir.Up, _navSeekIdx);
        _replayNavigator.Link(_navSpeedIdx, FocusNavigator.NavDir.Up, _navSeekIdx);
        _replayNavigator.Link(_navHighlightIdx, FocusNavigator.NavDir.Up, _navSeekIdx);
        if (toggleIdx >= 0)
            _replayNavigator.Link(toggleIdx, FocusNavigator.NavDir.Up, _navSeekIdx);
    }

    private void OnExit()
    {
        SceneNav.Pop();
    }

    private void OnPlayPause()
    {
        if (_player == null)
            return;

        if (_player.IsFinished)
        {
            // Reset to beginning
            var result = _player.SeekTo(0);
            RebuildBoardToState(result);
            _player.IsPlaying = true;
        }
        else
        {
            _player.IsPlaying = !_player.IsPlaying;
        }

        UpdatePlayPauseButton();
    }

    private void OnSpeedCycle()
    {
        if (_player == null)
            return;
        float speed = _player.CycleSpeed();
        if (_speedBtn != null)
            _speedBtn.text = FormatSpeed(speed);
    }

    private void OnHighlightToggle()
    {
        _highlightActive = !_highlightActive;

        if (_highlightActive)
        {
            if (_highlightBtn != null)
                _highlightBtn.AddToClassList("rh-highlight-btn--active");
            _boardView.UpdateClearableHighlights(_board, showTrails: true);
        }
        else
        {
            if (_highlightBtn != null)
                _highlightBtn.RemoveFromClassList("rh-highlight-btn--active");
            _boardView.ClearAllHighlights();
            _boardView.SetAllTrailsVisible(false);
        }
    }

    private void OnControlsToggle()
    {
        if (_controlsBar == null)
            return;

        _controlsVisible = !_controlsVisible;
        if (_controlsVisible)
        {
            _controlsBar.style.display = DisplayStyle.Flex;
            if (_controlsToggleIcon != null)
            {
                _controlsToggleIcon.RemoveFromClassList("rh-controls-toggle-icon--up");
                _controlsToggleIcon.AddToClassList("rh-controls-toggle-icon--down");
            }
            if (_controlsToggleBtn != null)
                _controlsToggleBtn.style.bottom = 100;
        }
        else
        {
            _controlsBar.style.display = DisplayStyle.None;
            if (_controlsToggleIcon != null)
            {
                _controlsToggleIcon.RemoveFromClassList("rh-controls-toggle-icon--down");
                _controlsToggleIcon.AddToClassList("rh-controls-toggle-icon--up");
            }
            if (_controlsToggleBtn != null)
                _controlsToggleBtn.style.bottom = 8;
        }
    }

    // --- Seek ---

    private void OnSeekPointerDown(PointerDownEvent evt)
    {
        _wasPlayingBeforeSeek = _player != null && _player.IsPlaying;
        if (_player != null)
            _player.IsPlaying = false;
        _isSeeking = true;
        _seekTrack.CapturePointer(evt.pointerId);
        ApplySeekFromPointer(evt.localPosition.x);
    }

    private void OnSeekPointerMove(PointerMoveEvent evt)
    {
        if (!_isSeeking)
            return;
        ApplySeekFromPointer(evt.localPosition.x);
    }

    private void OnSeekPointerUp(PointerUpEvent evt)
    {
        if (!_isSeeking)
            return;
        _isSeeking = false;
        _seekTrack.ReleasePointer(evt.pointerId);
        if (_wasPlayingBeforeSeek && _player != null)
        {
            _player.IsPlaying = true;
            UpdatePlayPauseButton();
        }
    }

    private void OnSeekCaptureOut(PointerCaptureOutEvent evt)
    {
        _isSeeking = false;
        if (_wasPlayingBeforeSeek && _player != null)
        {
            _player.IsPlaying = true;
            UpdatePlayPauseButton();
        }
    }

    private void SeekTo(float time)
    {
        if (_player == null)
            return;
        float duration = (float)_player.DisplayDuration;
        if (duration <= 0f)
            return;
        double normalized = Mathf.Clamp01(time / duration);
        var result = _player.SeekTo(normalized);
        RebuildBoardToState(result);
        UpdateSeekUI();
        UpdateTimeLabels();
    }

    private void ApplySeekFromPointer(float localX)
    {
        if (_seekTrack == null || _player == null)
            return;

        float trackWidth = _seekTrack.resolvedStyle.width;
        if (trackWidth <= 0)
            return;

        double normalized = System.Math.Max(0, System.Math.Min(1, localX / trackWidth));
        var result = _player.SeekTo(normalized);
        RebuildBoardToState(result);
        UpdateSeekUI();
        UpdateTimeLabels();
    }

    // --- Event execution ---

    private void ExecuteEvent(ReplayEvent evt)
    {
        if (evt.type == ReplayEventType.Clear)
        {
            var worldPos = new Vector3(evt.posX ?? 0f, evt.posY ?? 0f, 0f);
            Cell cell = BoardCoords.WorldToCell(worldPos, _board.Width, _board.Height);
            if (_board.Contains(cell))
            {
                Arrow arrow = _board.GetArrowAt(cell);
                if (arrow != null && _board.IsClearable(arrow))
                {
                    // Animated pull-out, then domain removal
                    _boardView.ClearArrowAnimated(arrow);
                    _board.RemoveArrow(arrow);
                }
                else
                {
                    Debug.LogWarning(
                        $"[ReplayViewController] ExecuteEvent Clear seq={evt.seq}: {(arrow == null ? "no arrow at" : "arrow not clearable at")} cell ({cell.X},{cell.Y})"
                    );
                }
            }
            else
            {
                Debug.LogWarning(
                    $"[ReplayViewController] ExecuteEvent Clear seq={evt.seq}: out-of-bounds cell ({cell.X},{cell.Y})"
                );
            }

            if (_tapPool != null)
                _tapPool.Spawn(worldPos, false);

            if (_highlightActive)
                _boardView.UpdateClearableHighlights(_board, showTrails: true);
        }
        else if (evt.type == ReplayEventType.Reject)
        {
            var worldPos = new Vector3(evt.posX ?? 0f, evt.posY ?? 0f, 0f);
            Cell cell = BoardCoords.WorldToCell(worldPos, _board.Width, _board.Height);
            if (_board.Contains(cell))
            {
                Arrow arrow = _board.GetArrowAt(cell);
                if (arrow != null)
                {
                    // Play bump animation toward blocker (same logic as BoardView.TryClearArrow)
                    ArrowView view = _boardView.GetArrowView(arrow);
                    if (view != null)
                    {
                        Arrow blocker = _board.GetFirstInRay(arrow);
                        if (blocker != null)
                        {
                            (int dx, int dy) = Arrow.GetDirectionStep(arrow.HeadDirection);
                            Cell cursor = new(arrow.HeadCell.X + dx, arrow.HeadCell.Y + dy);
                            int cellDistance = 1;
                            while (_board.Contains(cursor))
                            {
                                if (_board.GetArrowAt(cursor) == blocker)
                                    break;
                                cursor = new(cursor.X + dx, cursor.Y + dy);
                                cellDistance++;
                            }
                            float contactArcLength = cellDistance - 0.5f;
                            view.PlayBump(contactArcLength);
                        }
                        else
                        {
                            view.PlayRejectFlash();
                        }
                    }
                }
            }

            if (_tapPool != null)
                _tapPool.Spawn(worldPos, true);
        }
        else if (evt.type == ReplayEventType.Miss)
        {
            var worldPos = new Vector3(evt.posX ?? 0f, evt.posY ?? 0f, 0f);
            if (_tapPool != null)
                _tapPool.Spawn(worldPos, true);
        }
    }

    /// <summary>
    /// Rebuilds the board state after a seek operation by applying/undoing events.
    /// For backward seeks, rebuilds the full board from snapshot then replays forward.
    /// </summary>
    private void RebuildBoardToState(SeekResult result)
    {
        if (result.IsForward)
        {
            // Forward: just apply the events without animation
            foreach (var evt in result.EventsToApply)
            {
                if (evt.type == ReplayEventType.Clear)
                {
                    var worldPos = new Vector3(evt.posX ?? 0f, evt.posY ?? 0f, 0f);
                    Cell cell = BoardCoords.WorldToCell(worldPos, _board.Width, _board.Height);
                    if (_board.Contains(cell))
                    {
                        Arrow arrow = _board.GetArrowAt(cell);
                        if (arrow != null && _board.IsClearable(arrow))
                        {
                            _boardView.RemoveArrowView(arrow);
                            _board.RemoveArrow(arrow);
                        }
                    }
                }
            }
        }
        else if (result.IsBackward)
        {
            // Backward: rebuild from scratch — destroy current, re-restore, replay up to target
            RebuildBoardFromScratch();
        }

        if (_highlightActive)
            _boardView.UpdateClearableHighlights(_board, showTrails: true);
    }

    /// <summary>
    /// Fully rebuilds the board from the snapshot, then replays clears up to
    /// the current event index. Used for backward seek.
    /// </summary>
    private void RebuildBoardFromScratch()
    {
        Debug.Log(
            $"[ReplayViewController] RebuildBoardFromScratch: replaying {_player.ClearedEventIndices.Count} clears to reach seek target"
        );
        // Destroy existing board view
        if (_boardView != null)
            Destroy(_boardView.gameObject);

        // Recreate
        (_board, _boardView) = BoardSetupHelper.CreateBoardAndView(
            _replayData.boardWidth,
            _replayData.boardHeight,
            ThemeManager.Current ?? visualSettings
        );

        // Synchronous restore (seek must be instant)
        var snapshotArrows = new List<Arrow>(_boardSnapshot.Count);
        foreach (var cells in _boardSnapshot)
            snapshotArrows.Add(new Arrow(cells));

        // Exhaust the incremental restorer synchronously
        var restorer = _board.RestoreArrowsIncremental(snapshotArrows);
        int viewedCount = 0;
        while (restorer.MoveNext())
        {
            if (viewedCount < snapshotArrows.Count)
                _boardView.AddArrowView(snapshotArrows[viewedCount++]);
        }

        _boardView.ApplyColoring();

        // Replay cleared events up to current index
        int replayedClears = 0;
        foreach (int clearedIdx in _player.ClearedEventIndices)
        {
            var evt = GetTimedEvent(clearedIdx);
            if (evt != null && evt.type == ReplayEventType.Clear)
            {
                var worldPos = new Vector3(evt.posX ?? 0f, evt.posY ?? 0f, 0f);
                Cell cell = BoardCoords.WorldToCell(worldPos, _board.Width, _board.Height);
                if (_board.Contains(cell))
                {
                    Arrow arrow = _board.GetArrowAt(cell);
                    if (arrow != null && _board.IsClearable(arrow))
                    {
                        _boardView.RemoveArrowView(arrow);
                        _board.RemoveArrow(arrow);
                        replayedClears++;
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[ReplayViewController] RebuildBoardFromScratch: clear at ({cell.X},{cell.Y}) failed during replay — {(arrow == null ? "no arrow" : "not clearable")}"
                        );
                    }
                }
                else
                {
                    Debug.LogWarning(
                        $"[ReplayViewController] RebuildBoardFromScratch: clear event maps to out-of-bounds cell ({cell.X},{cell.Y})"
                    );
                }
            }
            else if (evt == null)
            {
                Debug.LogWarning(
                    $"[ReplayViewController] RebuildBoardFromScratch: GetTimedEvent({clearedIdx}) returned null"
                );
            }
        }
        Debug.Log(
            $"[ReplayViewController] RebuildBoardFromScratch complete: {replayedClears}/{_player.ClearedEventIndices.Count} clears applied, arrows remaining={_board.Arrows.Count}"
        );
    }

    /// <summary>
    /// Gets a timed event by its index in the ReplayPlayer's timed event list.
    /// Uses reflection-free approach via Advance/Seek result events.
    /// Since ReplayPlayer doesn't expose timed events by index, we track them
    /// by replaying the seek result's undo list.
    /// </summary>
    private ReplayEvent GetTimedEvent(int index)
    {
        // The ReplayPlayer stores timed events internally. We need to access them
        // to replay during backward seek. Use a full-seek approach instead.
        // This method is called after SeekTo already updated ClearedEventIndices,
        // so we can use the replay data's events directly.

        // Walk through replay data events to find the clear/reject event at this timed index
        int timedIdx = 0;
        bool tracking = false;
        foreach (var evt in _replayData.events)
        {
            if (evt.type == ReplayEventType.StartSolve)
                tracking = true;

            if (
                tracking
                && (evt.type == ReplayEventType.Clear || evt.type == ReplayEventType.Reject)
            )
            {
                if (timedIdx == index)
                    return evt;
                timedIdx++;
            }
        }
        return null;
    }

    // --- Camera input ---

    private void SetupCameraInput()
    {
        var km = KeybindManager.Instance;
        if (km == null)
            return;

        _pointAction = km.Point;
        _selectAction = km.Select;
        _zoomAction = km.Zoom;

        EnhancedTouchSupport.Enable();

        _dragThreshold = PlayerPrefs.GetFloat(
            GameSettings.DragThresholdPrefKey,
            GameSettings.DefaultDragThreshold
        );
    }

    private void HandleCameraInput()
    {
        if (_camCtrl == null || _selectAction == null)
            return;

        HandleTouchPinch();
        HandleScrollZoom();
        HandlePan();
    }

    private void HandlePan()
    {
        if (_selectAction.WasPressedThisFrame())
        {
            _pressStartScreen = _pointAction.ReadValue<Vector2>();
            _pressStartWorld = _camCtrl.Cam.ScreenToWorldPoint(_pressStartScreen);
            _isDragging = false;
            _isPressed = true;
        }

        if (_isPressed && _selectAction.IsPressed())
        {
            Vector2 currentScreen = _pointAction.ReadValue<Vector2>();
            float dist = Vector2.Distance(_pressStartScreen, currentScreen);

            if (!_isDragging && dist >= _dragThreshold)
            {
                _isDragging = true;
                _pressStartWorld = _camCtrl.Cam.ScreenToWorldPoint(currentScreen);
            }

            if (_isDragging)
            {
                Vector3 currentWorld = _camCtrl.Cam.ScreenToWorldPoint(currentScreen);
                _camCtrl.Pan(_pressStartWorld - currentWorld);
                _pressStartWorld = _camCtrl.Cam.ScreenToWorldPoint(currentScreen);
            }
        }

        if (_isPressed && _selectAction.WasReleasedThisFrame())
        {
            _isPressed = false;
            _isDragging = false;
        }
    }

    private void HandleScrollZoom()
    {
        if (_zoomAction == null)
            return;
        float scroll = _zoomAction.ReadValue<float>();
        if (Mathf.Abs(scroll) > 0.01f)
            _camCtrl.Zoom(scroll);
    }

    private void HandleTouchPinch()
    {
        if (Touch.activeTouches.Count < 2)
            return;

        var t0 = Touch.activeTouches[0];
        var t1 = Touch.activeTouches[1];

        Vector2 prevPos0 = t0.screenPosition - t0.delta;
        Vector2 prevPos1 = t1.screenPosition - t1.delta;

        float prevDist = Vector2.Distance(prevPos0, prevPos1);
        float currDist = Vector2.Distance(t0.screenPosition, t1.screenPosition);

        if (prevDist > 0.01f)
            _camCtrl.PinchZoom(currDist / prevDist);
    }

    // --- Loading ---

    private void ShowLoading()
    {
        if (_loadingOverlay != null)
            _loadingOverlay.style.display = DisplayStyle.Flex;
    }

    private void HideLoading()
    {
        if (_loadingOverlay != null)
            _loadingOverlay.style.display = DisplayStyle.None;
    }

    private void UpdateLoadingProgress(float progress)
    {
        if (_loadingBarFill != null)
            _loadingBarFill.style.width = new StyleLength(
                new Length(progress * 100f, LengthUnit.Percent)
            );
        if (_loadingPercent != null)
            _loadingPercent.text = Mathf.RoundToInt(progress * 100f) + "%";
    }

    // --- UI updates ---

    private void UpdateSeekUI()
    {
        if (_player == null)
            return;

        float norm = (float)_player.NormalizedTime;
        float pct = norm * 100f;

        if (_seekFill != null)
            _seekFill.style.width = new StyleLength(new Length(pct, LengthUnit.Percent));
        if (_seekHandle != null)
            _seekHandle.style.left = new StyleLength(new Length(pct, LengthUnit.Percent));
    }

    private void UpdateTimeLabels()
    {
        if (_player == null)
            return;

        if (_timeCurrent != null)
        {
            double displayTime = System.Math.Min(_player.CurrentTime, _player.DisplayDuration);
            _timeCurrent.text = FormatTime(displayTime);
        }
    }

    private void UpdatePlayPauseButton()
    {
        if (_playPauseIcon == null)
            return;
        bool isPlaying = _player != null && _player.IsPlaying;
        if (isPlaying)
        {
            _playPauseIcon.RemoveFromClassList("rh-play-icon--play");
            _playPauseIcon.AddToClassList("rh-play-icon--pause");
        }
        else
        {
            _playPauseIcon.RemoveFromClassList("rh-play-icon--pause");
            _playPauseIcon.AddToClassList("rh-play-icon--play");
        }
    }

    private static string FormatTime(double seconds)
    {
        int totalSec = (int)seconds;
        int min = totalSec / 60;
        int sec = totalSec % 60;
        return $"{min}:{sec:D2}";
    }

    private static string FormatSpeed(float speed)
    {
        if (speed == (int)speed)
            return $"{(int)speed}x";
        return $"{speed:0.#}x";
    }
}
