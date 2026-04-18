using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    // Co-op mode state (active when GameSettings.ActiveLobbyCode is set)
    private bool _isCoopMode;
    private string _coopLobbyCode;
    private CoopClient _coopClient;
    private byte[] _coopSnapshotData;
    private CoopSession _coopSession;
    private Guid _coopUserId;
    private float _heartbeatAccum;
    private const float HeartbeatIntervalSec = 15f;

    // Auto-reconnect state (Phase 6). When the CoopClient disconnects
    // unexpectedly (not via user back-out), schedule a reconnect with
    // exponential backoff.
    private bool _coopShouldReconnect;
    private int _coopReconnectAttempt;
    private float _coopReconnectAt;
    private bool _coopReconnectInFlight;

    // Per-clear tap indicator pool for co-op: remote taps spawn a ring in
    // the clearer's color. Lazy-initialized in co-op mode only.
    private TapIndicatorPool _coopTapPool;

    // Per-player solve timer for co-op (Phase 7). Starts on first local
    // accepted clear, reports every 5 s via timer_update.
    private CoopPlayerTimer _coopPlayerTimer;

    // Roster sidebar component for co-op (Phase 7).
    private CoopSidebar _coopSidebar;

    // Previous roster snapshot, used to diff for "joined" toasts (Phase 7).
    private HashSet<Guid> _previousRosterIds = new();
    private bool _rosterDiffPrimed;
    private float _lastRateLimitToastAt = -999f;

    // 1, 2, 4, 8, 16, 30 seconds (capped).
    private static readonly float[] CoopReconnectDelays = { 1f, 2f, 4f, 8f, 16f, 30f };

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
        _coopSidebar?.Dispose();
        _coopPlayerTimer?.Dispose();
        _coopSession?.Dispose();
        _coopClient?.Dispose();
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
        _coopClient?.Update();

        // Co-op heartbeat loop (Phase 6). 15s cadence with current focus state
        // and last-input timestamp; the server uses these for AFK detection.
        if (_coopSession != null && _coopClient != null && _coopClient.IsConnected)
        {
            _heartbeatAccum += Time.unscaledDeltaTime;
            if (_heartbeatAccum >= HeartbeatIntervalSec)
            {
                _heartbeatAccum = 0f;
                var lastInput =
                    _inputHandler != null ? _inputHandler.LastInputTimeUtc : DateTime.UtcNow;
                _ = _coopClient.SendAsync(CoopMessage.Heartbeat(Application.isFocused, lastInput));
            }
        }

        // Per-player solve timer (Phase 7). Ticks only while focused; emits
        // timer_update every 5 s so other players see our time advance.
        if (_coopPlayerTimer != null)
            _coopPlayerTimer.Tick(Time.unscaledDeltaTime);

        // Auto-reconnect driver: fires a single reconnect attempt when the
        // backoff timer expires. Additional retries are scheduled by the
        // Disconnected handler when a reconnect itself fails.
        if (
            _coopShouldReconnect
            && _coopClient != null
            && !_coopClient.IsConnected
            && !_coopReconnectInFlight
            && _coopReconnectAt > 0f
            && Time.unscaledTime >= _coopReconnectAt
        )
        {
            _coopReconnectInFlight = true;
            _coopReconnectAt = 0f;
            _ = AttemptCoopReconnectAsync();
        }

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
        // Check for co-op mode FIRST — it bypasses all solo parameter resolution.
        _coopLobbyCode = GameSettings.ConsumeActiveLobbyCode();
        _isCoopMode = !string.IsNullOrEmpty(_coopLobbyCode);

        if (_isCoopMode)
        {
            yield return CoopSetup();
            yield break;
        }

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
        bool hasSnapshot = priorData != null && !string.IsNullOrEmpty(priorData.boardSnapshot);

        CreateBoardAndView();
        SetupCamera();
        _boardView.SetCameraController(_camCtrl);

        if (hasSnapshot)
        {
            _initialBoardSnapshot = priorData.GetSnapshotArrows();
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

    // --- Co-op mode setup ---

    private IEnumerator CoopSetup()
    {
        ResolveHudElements();
        ShowLoading("Connecting...");
        yield return null;

        var api = new ApiClient();
        if (!api.IsLoggedIn)
        {
            Debug.LogError("[GameController] Co-op mode requires login.");
            SceneNav.Pop();
            yield break;
        }

        // Connect WebSocket and send hello.
        _coopClient = new CoopClient();
        _coopSnapshotData = null;
        bool failed = false;
        string failReason = null;

        _coopClient.MessageReceived += msg =>
        {
            switch (msg.Type)
            {
                case "welcome":
                    var uidStr = msg.Payload?.Value<string>("yourUserId");
                    if (Guid.TryParse(uidStr, out var uid))
                        _coopUserId = uid;
                    Debug.Log(
                        $"[GameController] Co-op welcome for lobby {_coopLobbyCode} (you={_coopUserId})"
                    );
                    break;
                case "gen_progress":
                    var pct = msg.Payload?.Value<int>("pct") ?? 0;
                    _loadProgress = pct / 100f;
                    UpdateLoadingLabel($"Generating... {pct}%");
                    break;
                case "gen_complete":
                    _loadProgress = 1f;
                    UpdateLoadingLabel("Board ready!");
                    break;
                case "snapshot":
                    // Binary frame follows — handled below.
                    break;
                case "lobby_failed":
                    failed = true;
                    failReason = "Board generation failed.";
                    break;
                case "disconnect":
                    failed = true;
                    failReason = msg.Payload?.Value<string>("reason") ?? "Disconnected";
                    break;
            }
        };

        _coopClient.BinaryReceived += data =>
        {
            _coopSnapshotData = data;
        };

        _coopClient.Disconnected += reason =>
        {
            if (_coopSnapshotData == null)
            {
                failed = true;
                failReason = $"Disconnected: {reason}";
                return;
            }

            // Unexpected disconnect after we were playing — schedule reconnect.
            // Server-initiated disconnects for terminal states (rate_limited,
            // lobby_completed, registration_cap) should NOT retry.
            if (!_coopShouldReconnect)
                return;
            if (
                reason != null
                && (
                    reason.Contains("rate_limited")
                    || reason.Contains("completed")
                    || reason.Contains("deleted")
                    || reason.Contains("cap")
                )
            )
                return;

            _coopReconnectInFlight = false;
            _coopReconnectAt = Time.unscaledTime + GetReconnectDelay(_coopReconnectAttempt);
            _coopReconnectAttempt++;
            if (_coopSession != null && GlobalToast.Instance != null)
                GlobalToast.Instance.ShowInfo("Reconnecting...");
        };

        var connectTask = _coopClient.ConnectAsync(api.BaseWsUrl, _coopLobbyCode, api.Token);
        while (!connectTask.IsCompleted)
            yield return null;
        if (connectTask.IsFaulted)
        {
            Debug.LogError($"[GameController] Co-op connect failed: {connectTask.Exception}");
            SceneNav.Pop();
            yield break;
        }

        var helloTask = _coopClient.SendAsync(CoopMessage.Hello(0));
        while (!helloTask.IsCompleted)
            yield return null;
        if (helloTask.IsFaulted)
        {
            Debug.LogError($"[GameController] Co-op hello failed: {helloTask.Exception}");
            SceneNav.Pop();
            yield break;
        }

        // Wait for snapshot binary frame (or failure).
        while (_coopSnapshotData == null && !failed)
        {
            if (_cancelRequested)
            {
                _coopClient.Dispose();
                _coopClient = null;
                SceneNav.Pop();
                yield break;
            }
            yield return null;
        }

        if (failed)
        {
            Debug.LogWarning($"[GameController] Co-op failed: {failReason}");
            UpdateLoadingLabel(failReason ?? "Failed");
            // Wait a beat so the user sees the message, then pop.
            yield return new WaitForSeconds(2f);
            SceneNav.Pop();
            yield break;
        }

        // Decode snapshot and build the board.
        UpdateLoadingLabel("Loading board...");
        yield return null;

        Board decodedBoard = null;
        try
        {
            decodedBoard = BinarySnapshot.DecodeFull(_coopSnapshotData);
        }
        catch (Exception e)
        {
            Debug.LogError($"[GameController] Failed to decode co-op snapshot: {e}");
        }
        if (decodedBoard == null)
        {
            SceneNav.Pop();
            yield break;
        }
        _board = decodedBoard;
        _w = _board.Width;
        _h = _board.Height;

        Debug.Log($"[GameController] Co-op board decoded: {_w}x{_h}, {_board.Arrows.Count} arrows");

        // Create the view from the already-populated board.
        var vs = ThemeManager.Current ?? visualSettings;
        var boardGo = new GameObject("BoardView");
        _boardView = boardGo.AddComponent<BoardView>();
        _boardView.Init(_board, vs, spawnArrows: true);

        SetupCamera();
        _boardView.SetCameraController(_camCtrl);
        _boardView.ApplyColoring();

        HideLoading();

        // Enable auto-reconnect now that we have a working session.
        _coopShouldReconnect = true;

        // Tap indicator pool for remote-player taps. Uses the theme-aware
        // ring color as a fallback when no player color is known.
        _coopTapPool = new TapIndicatorPool(
            sprite: null,
            duration: 0.4f,
            maxScale: 1.5f,
            parent: transform
        );

        // Pass the input handler's last-input timestamp so the timer pauses
        // during extended idle even when the window keeps focus. Purely
        // local; not surfaced as a UI status.
        _coopPlayerTimer = new CoopPlayerTimer(
            _coopClient,
            isFocusedProvider: () => Application.isFocused,
            lastInputProvider: () =>
                _inputHandler != null ? _inputHandler.LastInputTimeUtc : DateTime.UtcNow
        );

        // Create the session wrapper + wire server event handlers.
        _coopSession = new CoopSession(_coopClient, _board, _coopUserId);
        _coopSession.RemoteCleared += OnCoopRemoteCleared;
        _coopSession.RemoteRejectedDep += OnCoopRemoteRejectedDep;
        _coopSession.LocalRejectedRace += _ =>
        { /* silent; arrow was already animated away */
        };
        _coopSession.LocalRejectedRate += _ =>
        {
            // Throttle to once per 3 s so a burst doesn't stack toasts.
            if (Time.unscaledTime - _lastRateLimitToastAt < 3f)
                return;
            _lastRateLimitToastAt = Time.unscaledTime;
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError("Slow down");
        };
        _coopSession.LobbyCompleted += OnCoopLobbyCompleted;
        _coopSession.RosterUpdated += OnCoopRosterUpdated;

        // Mount the sidebar into the HUD root. Safe to do before WireHud()
        // since we only need the UIDocument's root.
        if (hudUIDocument != null && hudUIDocument.rootVisualElement != null)
            _coopSidebar = new CoopSidebar(_coopSession, hudUIDocument.rootVisualElement);

        WireHud();
        WireInput();
        // Skip WireVictory — co-op completion is handled by the server.
    }

    private void OnCoopRemoteCleared(CoopSession.ClearedEvent evt)
    {
        if (evt.IsLocal)
        {
            // Our own accepted tap: start the per-player timer on the first
            // clear, and schedule a prompt timer_update so the sidebar row
            // advances immediately.
            if (_coopPlayerTimer != null)
                _coopPlayerTimer.NoteAcceptedClear();
            return; // already animated locally via optimistic clear
        }
        if (evt.Arrow == null || _boardView == null)
            return;

        // Remote clear: tint the pull-out in the clearer's color and spawn a
        // tap indicator ring in the same color, so the action is attributed
        // visually without needing an always-on player overlay.
        Color tint = new Color32(
            evt.Color.r,
            evt.Color.g,
            evt.Color.b,
            evt.Color.a == 0 ? (byte)255 : evt.Color.a
        );
        _boardView.ClearArrowAnimated(evt.Arrow, flashColor: tint);
        if (_coopTapPool != null)
            _coopTapPool.Spawn(evt.TapWorld, tint);
    }

    private void OnCoopRemoteRejectedDep(CoopSession.RejectedDepEvent evt)
    {
        // Server broadcast a dep rejection. Play the standard blocked-tap
        // flash on that arrow for all players. If it was OUR attempt, the
        // flash doubles as a rollback signal since we hadn't actually
        // removed the arrow locally (TrySubmitClear only reads it).
        if (evt.Arrow != null && _boardView != null)
        {
            // TryClearArrow plays the reject animation if the arrow isn't clearable.
            _boardView.TryClearArrow(evt.Arrow);
        }
    }

    private void OnCoopLobbyCompleted()
    {
        if (GlobalToast.Instance != null)
            GlobalToast.Instance.ShowInfo("Board cleared!");
        if (_inputHandler != null)
            _inputHandler.SetInputEnabled(false);
    }

    /// <summary>
    /// Roster-diff watcher: toasts "name joined" whenever a new user id
    /// appears in the roster after the initial snapshot. The initial
    /// <c>roster_full</c> (priming) doesn't fire joined toasts for already-
    /// present players.
    /// </summary>
    private void OnCoopRosterUpdated()
    {
        if (_coopSession == null)
            return;

        if (!_rosterDiffPrimed)
        {
            _previousRosterIds = new HashSet<Guid>(_coopSession.Roster.Keys);
            _rosterDiffPrimed = true;
            return;
        }

        foreach (var kvp in _coopSession.Roster)
        {
            if (kvp.Key == _coopUserId)
                continue;
            if (_previousRosterIds.Contains(kvp.Key))
                continue;
            var name = string.IsNullOrEmpty(kvp.Value.DisplayName)
                ? "Someone"
                : kvp.Value.DisplayName;
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowInfo($"{name} joined");
        }

        _previousRosterIds = new HashSet<Guid>(_coopSession.Roster.Keys);
    }

    private static float GetReconnectDelay(int attempt)
    {
        if (attempt < 0)
            attempt = 0;
        if (attempt >= CoopReconnectDelays.Length)
            attempt = CoopReconnectDelays.Length - 1;
        return CoopReconnectDelays[attempt];
    }

    private const int ReconnectToastGiveUpAttempt = 6;

    private async Task AttemptCoopReconnectAsync()
    {
        try
        {
            await _coopClient.ReconnectAsync();
            await _coopClient.SendAsync(CoopMessage.Hello(0));
            _coopReconnectAttempt = 0;
            _coopReconnectInFlight = false;
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowInfo("Reconnected");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameController] Co-op reconnect failed: {e.Message}");
            _coopReconnectInFlight = false;
            _coopReconnectAt = Time.unscaledTime + GetReconnectDelay(_coopReconnectAttempt);
            _coopReconnectAttempt++;

            // After walking the full backoff ladder, surface a persistent
            // "lost connection" toast so the player knows we're still
            // trying but something's wrong — auto-reconnect keeps going.
            if (
                _coopReconnectAttempt == ReconnectToastGiveUpAttempt
                && GlobalToast.Instance != null
            )
            {
                GlobalToast.Instance.ShowError("Lost connection — still retrying");
            }
        }
    }

    private bool OnCoopTap(Cell cell, Vector3 tapWorld)
    {
        if (_coopSession == null)
            return true;
        var arrow = _coopSession.TrySubmitClear(cell, tapWorld);
        if (arrow != null && _boardView != null)
        {
            // Optimistic animation — user sees the arrow vanish immediately.
            // Server accept (`cleared`) does nothing further for our tap;
            // server reject-dep plays the reject flash; server reject-race
            // is silent (arrow was already gone).
            _boardView.TryClearArrow(arrow);
        }
        return true;
    }

    private void UpdateLoadingLabel(string text)
    {
        if (_loadingPercent != null)
            _loadingPercent.text = text;
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
        // _initialBoardSnapshot was populated from priorData.GetSnapshotArrows() in GenerateAndSetup.
        int totalArrows = _initialBoardSnapshot.Count;
        Debug.Log($"[GameController] RestoreBoard: restoring {totalArrows} arrows from snapshot");
        int totalSteps = totalArrows * 2;
        var restorer = BoardSetupHelper.RestoreBoardFromSnapshot(
            _board,
            _boardView,
            _initialBoardSnapshot,
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

        // Phase-weighted progress (shared with the server generation worker
        // via GenerationProgress in the domain layer). Client has an extra
        // view-rebuild phase between Compacting and Finalizing that the server
        // doesn't — ClientWeights accounts for it.
        var weights = GenerationProgress.ClientWeights;
        // Track Arrow refs so we can remove their views after compaction
        var spawnedArrows = new List<Arrow>();
        int arrowsBeforeCompaction = 0;
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

            // Progress calculation per phase (via shared GenerationProgress helper).
            if (rebuildingViews)
            {
                float rebuildRatio = rebuildTotal > 0 ? (float)rebuildIndex / rebuildTotal : 1f;
                _loadProgress = GenerationProgress.ForRebuildingViews(rebuildRatio, weights);
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
                        _loadProgress = GenerationProgress.ForGenerating(depletion, weights);
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
                        _loadProgress = GenerationProgress.ForCompacting(
                            mergesCompleted: arrowsBeforeCompaction - _board.Arrows.Count,
                            arrowsBeforeCompaction: arrowsBeforeCompaction,
                            timeRatio: timeRatio,
                            w: weights
                        );
                        break;
                    }
                    case GenerationPhase.Finalizing:
                    {
                        int arrowCount = _board.Arrows.Count;
                        float finalizeRatio =
                            arrowCount > 0 && generator.Current is int finalized
                                ? (float)finalized / arrowCount
                                : 0f;
                        _loadProgress = GenerationProgress.ForFinalizing(finalizeRatio, weights);
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
            if (_isCoopMode)
            {
                _retryBtn.style.display = DisplayStyle.None;
            }
            else
            {
                _retryBtn.clickable = new Clickable(() => { });
                _retryBtn.clicked += OnRetryClicked;
            }
        }

        if (_timer != null)
        {
            var timerView = gameObject.AddComponent<GameTimerView>();
            timerView.Init(_timer, hudUIDocument, inspectionWarningThreshold);
        }
        else if (_timerLabel != null)
        {
            // Co-op mode: hide the solo timer label.
            _timerLabel.style.display = DisplayStyle.None;
        }

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
            onToggleTrail: ToggleTrail,
            onTapAttempt: _isCoopMode ? (Func<Cell, Vector3, bool>)OnCoopTap : null
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

    private void ReturnToModeSelect()
    {
        // Stop reconnect attempts before tearing down the scene.
        _coopShouldReconnect = false;
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
