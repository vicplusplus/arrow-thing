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

    // Game state — _board/_boardView/_camCtrl are populated by the active
    // mode's Setup() into a GameContext, then copied here for shared
    // consumers (OnThemeChanged, WireInput, leave/escape teardown).
    private Board _board;
    private BoardView _boardView;
    private CameraController _camCtrl;
    private InputHandler _inputHandler;

    // Classic-only run state (timer, recorder, game-id, seed, dimensions,
    // inspection duration) lives on ClassicMode after phase 2D. WireInput
    // reads timer/recorder via cast.

    // Co-op state (WebSocket session, sidebar, reconnect, results overlay,
    // tap submission, roster diff, heartbeat) lives on CoopMode after
    // phase 2E. GameController only knows the active mode is "coop" via
    // peeking GameSettings.ActiveLobbyCode in CreateMode.

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

    // _retryModal moved to ClassicMode (built in OnHudWired).
    private FocusNavigator _focusNavigator;
    private VisualElement _cancelGenModal;
    private float _loadProgress;
    private bool _loadingActive;
    private float _loadingFadeStart;

    // Active mode (Classic, Coop, future Endless/PvP). Picked once in Awake
    // based on GameSettings; all per-frame and per-event mode-specific
    // dispatch routes through this. Replaces the previously scattered
    // `if (_isCoopMode)` branches across Update / WireHud / WireInput /
    // WireVictory.
    private IGameMode _mode;

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
        _mode?.Dispose();
        _focusNavigator?.Dispose();
        SettingsController.IsOpenChanged -= OnSettingsOpenChanged;
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>
    /// Picks the active mode by peeking <see cref="GameSettings.ActiveLobbyCode"/>
    /// (CoopMode itself consumes the value during <see cref="CoopMode.Setup"/>).
    /// Mode is added as a sibling component so it can carry coroutines, then
    /// bound to this controller.
    /// </summary>
    private IGameMode CreateMode()
    {
        if (!string.IsNullOrEmpty(GameSettings.ActiveLobbyCode))
        {
            var coop = gameObject.AddComponent<CoopMode>();
            coop.Bind(this);
            return coop;
        }
        var classic = gameObject.AddComponent<ClassicMode>();
        classic.Bind(this);
        return classic;
    }

    // ---- Shared scene state accessors (read by mode classes) -------------

    /// <summary>Internal: HUD retry button so <see cref="CoopMode.OnHudWired"/> can hide it.</summary>
    internal Button HudRetryButton => _retryBtn;

    /// <summary>Internal: HUD back button so <see cref="ClassicMode.WireRunFlow"/> can hide it on victory.</summary>
    internal Button BackButton => _backBtn;

    /// <summary>Internal: live board (set after mode.Setup populates GameContext).</summary>
    internal Board CurrentBoard => _board;

    /// <summary>Internal: live BoardView, for ClassicMode victory wiring.</summary>
    internal BoardView BoardViewRef => _boardView;

    /// <summary>Internal: live CameraController, for ClassicMode victory wiring.</summary>
    internal CameraController CameraControllerRef => _camCtrl;

    /// <summary>Internal: live <see cref="InputHandler"/> after WireInput runs.</summary>
    internal InputHandler ActiveInputHandler => _inputHandler;

    /// <summary>Internal: HUD UIDocument, so modes can resolve their own elements.</summary>
    internal UIDocument HudDocument => hudUIDocument;

    /// <summary>Internal: Victory UIDocument SerializeField, consumed by ClassicMode.</summary>
    internal UIDocument VictoryDocument => victoryUIDocument;

    /// <summary>Internal: inspection-warning threshold SerializeField, consumed by ClassicMode timer view.</summary>
    internal float InspectionWarningThreshold => inspectionWarningThreshold;

    // ---- Editor-override SerializeField accessors (classic only) ---------

    internal int EditorBoardWidth => boardWidth;
    internal int EditorBoardHeight => boardHeight;
    internal int EditorMaxArrowLength => maxArrowLength;
    internal bool EditorUseRandomSeed => useRandomSeed;
    internal int EditorSeed => seed;
    internal float EditorInspectionDuration => inspectionDuration;

    // ---- Loading overlay control (called from mode Setup) ----------------

    internal void ShowLoadingInternal(string label) => ShowLoading(label);

    internal void HideLoadingInternal() => HideLoading();

    internal void SetLoadProgress(float p) => _loadProgress = p;

    internal bool CancelRequested => _cancelRequested;

    /// <summary>Internal: marks the victory sequence as started so Escape stops opening the leave modal.</summary>
    internal void MarkVictoryStarted() => _victoryStarted = true;

    /// <summary>Internal: triggers a same-mode scene reload (Ctrl+R / retry-confirmed).</summary>
    internal void RequestQuickReset() => OnQuickReset();

    /// <summary>Internal: returns to the main menu (Leave / topout).</summary>
    internal void RequestReturnToModeSelect() => ReturnToModeSelect();

    /// <summary>Internal: <c>UpdateLoadingLabel</c> exposed for CoopMode setup status messages.</summary>
    internal void UpdateLoadingLabelInternal(string text) => UpdateLoadingLabel(text);

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
        // Per-mode tick: coop runs WS pump / heartbeat / reconnect / player
        // timer here (CoopMode is created in GenerateAndSetup before its
        // Setup awaits any WS messages, so Tick fires from the loading
        // window onward); future endless / pvp run their spawn timers here.
        _mode?.Tick();

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
        // Pick + bind the active mode (peeks GameSettings.ActiveLobbyCode for
        // coop detection). All per-frame and per-event mode-specific dispatch
        // (Update tick, HUD tweaks, tap handler, run-flow wiring) goes through
        // this from here on.
        _mode = CreateMode();

        // Shared HUD element lookup runs first so mode.Setup can drive the
        // loading overlay. Then delegate the entire setup pipeline into the
        // mode (classic: parameter resolution + generation/restore + recorder
        // + timer; coop: WS connect + snapshot decode + session wiring).
        ResolveHudElements();

        var ctx = new GameContext(
            controller: this,
            mainCamera: mainCamera,
            visualSettings: visualSettings,
            hudDocument: hudUIDocument,
            reportLoadProgress: (p, _) => _loadProgress = p,
            isCancelRequested: () => _cancelRequested
        );

        yield return _mode.Setup(ctx);

        // Mode's Setup may have bailed (deferred-resume save not found, gen
        // produced 0 arrows, user cancelled, coop WS connect failed). In all
        // those cases the mode pops the scene before returning; ctx.Board
        // stays null.
        if (ctx.Board == null)
            yield break;

        _board = ctx.Board;
        _boardView = ctx.BoardView;
        _camCtrl = ctx.CameraController;

        WireHud();
        WireInput();
        // Mode owns the run-flow: classic wires VictoryController; coop is a
        // no-op (server-driven completion); endless wires topout result.
        _mode?.WireRunFlow();
    }

    private void UpdateLoadingLabel(string text)
    {
        if (_loadingPercent != null)
            _loadingPercent.text = text;
    }

    // --- Shared HUD element lookup ---
    // Classic setup (parameter resolution, generation/restore, recorder,
    // timer, victory wiring) lives on ClassicMode after phase 2D.
    // Coop setup (WS connect, snapshot decode, session, sidebar, results
    // overlay, reconnect, tap submission) lives on CoopMode after phase 2E.

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

    // --- HUD wiring ---

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

        // Retry modal is owned by ClassicMode (built in its OnHudWired).
        // Coop never builds it because the retry button itself is hidden.

        if (_backBtn != null)
        {
            _backBtn.clickable = new Clickable(() => { });
            _backBtn.clicked += ShowLeave;
        }

        if (_retryBtn != null)
        {
            // Default: wire retry → forward to active mode (only ClassicMode
            // implements it; CoopMode hides the button entirely via OnHudWired).
            _retryBtn.clickable = new Clickable(() => { });
            _retryBtn.clicked += OnRetryClickedDispatch;
        }

        // Timer view construction lives in ClassicMode.OnHudWired (it owns
        // the GameTimer). Coop hides the timer-label via CoopMode.OnHudWired
        // because it has no solo timer.

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
                            OnRetryClickedDispatch();
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

        // Per-mode HUD tweaks (e.g. CoopMode hides the retry button).
        _mode?.OnHudWired();
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
        // Mode owns timer/recorder/autosave — InputHandler hands it the
        // tap outcome via OnTapResult and stays mode-agnostic itself.
        _inputHandler.Init(
            _board,
            _boardView,
            _camCtrl,
            dragThreshold,
            onTapResult: _mode != null ? _mode.OnTapResult : (Action<TapResult>)null,
            onQuickReset: OnQuickReset,
            onQuickSave: _mode?.OnQuickSaveHandler,
            onToggleTrail: ToggleTrail,
            onTapAttempt: _mode?.TapAttemptHandler,
            hudUIDocument: hudUIDocument
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

    /// <summary>
    /// Dispatches the retry-button click to the active mode if it implements
    /// retry behavior. ClassicMode shows the retry confirmation modal; coop
    /// hides the button entirely so this never fires from coop.
    /// </summary>
    private void OnRetryClickedDispatch()
    {
        if (_mode is ClassicMode classic)
            classic.OnRetryClickedExternal();
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

        if (_mode != null && _mode.WouldOverwriteDifferentSave)
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
        // Decision tree delegated to the active mode:
        //  - "would overwrite different save" → SaveAndLeave (mode persists, then pop)
        //  - "supports save AND has in-progress changes" → SaveAndLeave
        //  - else → just leave
        if (_mode != null && _mode.WouldOverwriteDifferentSave)
            _mode.SaveAndLeave();
        else if (_mode != null && _mode.SupportsSaveOnLeave && _mode.HasInProgressChanges)
            _mode.SaveAndLeave();
        else
            ReturnToModeSelect();
    }

    private void OnLeaveCancel()
    {
        if (_mode != null && _mode.WouldOverwriteDifferentSave)
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

    // WireVictoryDefault moved into ClassicMode.WireRunFlow (phase 2D).

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
                // Also pop directly: if the load coroutine has already died
                // (e.g. a shader/decode exception) it's no longer polling the
                // flag, and the user is stranded on the loading overlay.
                ReturnToModeSelect();
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

    private void OnLeaveDismiss()
    {
        _leaveModal?.Hide();
        if (_inputHandler != null)
            _inputHandler.SetInputEnabled(true);
    }

    private void ReturnToModeSelect()
    {
        // Dispose the active mode before pop so coop's reconnect driver halts
        // immediately (one-frame race window before OnDestroy fires otherwise).
        // Mode Dispose is idempotent — OnDestroy still calls it.
        _mode?.Dispose();
        SceneNav.Pop();
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
