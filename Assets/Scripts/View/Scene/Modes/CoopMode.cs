using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Co-op mode: server-authoritative real-time collaboration. Board comes from
/// a server WebSocket snapshot (no client-side generation); clears go through
/// a <see cref="CoopSession"/>; completion is server-driven (no client victory
/// wiring); a roster sidebar + per-player timer run alongside the gameplay HUD.
///
/// Phase 2E: CoopMode now owns the entire co-op pipeline — WebSocket lifecycle
/// (connect/hello/snapshot/heartbeat/reconnect), session wrapper, sidebar,
/// results overlay, completed-lobby read-only path, remote-clear / remote-reject
/// handlers, roster diff, tap submission, and disposal. <see cref="GameController"/>
/// only needs to add CoopMode as a component when coop is active and call
/// <see cref="Setup"/>.
/// </summary>
public sealed class CoopMode : MonoBehaviour, IGameMode
{
    private GameController _controller;

    // WebSocket session state.
    private string _coopLobbyCode;
    private CoopClient _coopClient;
    private byte[] _coopSnapshotData;
    private CoopSession _coopSession;
    private Guid _coopUserId;
    private float _heartbeatAccum;
    private const float HeartbeatIntervalSec = 15f;

    // Auto-reconnect state. When the CoopClient disconnects unexpectedly
    // (not via user back-out), schedule a reconnect with exponential backoff.
    private bool _coopShouldReconnect;
    private int _coopReconnectAttempt;
    private float _coopReconnectAt;
    private bool _coopReconnectInFlight;
    private static readonly float[] CoopReconnectDelays = { 1f, 2f, 4f, 8f, 16f, 30f };
    private const int ReconnectToastGiveUpAttempt = 6;

    // Per-clear tap indicator pool: remote taps spawn a ring in the clearer's color.
    private TapIndicatorPool _coopTapPool;

    // Per-player solve timer. Starts on first local accepted clear; emits
    // timer_update every 5 s so other players see our time advance.
    private CoopPlayerTimer _coopPlayerTimer;

    // Roster sidebar.
    private CoopSidebar _coopSidebar;

    // Completion results overlay, shown on lobby_completed.
    private CoopResultsScreen _coopResults;

    // Previous roster snapshot, used to diff for "joined" toasts.
    private HashSet<Guid> _previousRosterIds = new();
    private bool _rosterDiffPrimed;
    private float _lastRateLimitToastAt = -999f;

    public string Name => "Coop";

    /// <summary>Wired by GameController immediately after AddComponent.</summary>
    public void Bind(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public IEnumerator Setup(GameContext context) => RunSetup(context);

    public void Tick()
    {
        // WebSocket message pump — runs during the loading window too because
        // CoopSetup waits on snapshot/hello messages that arrive via this pump.
        // (GameController.Update calls _mode?.Tick() unconditionally.)
        _coopClient?.Update();

        // Co-op heartbeat loop. 15s cadence with current focus state and
        // last-input timestamp; the server uses these for AFK detection.
        if (_coopSession != null && _coopClient != null && _coopClient.IsConnected)
        {
            _heartbeatAccum += Time.unscaledDeltaTime;
            if (_heartbeatAccum >= HeartbeatIntervalSec)
            {
                _heartbeatAccum = 0f;
                var input = _controller.ActiveInputHandler;
                var lastInput = input != null ? input.LastInputTimeUtc : DateTime.UtcNow;
                _ = _coopClient.SendAsync(CoopMessage.Heartbeat(Application.isFocused, lastInput));
            }
        }

        // Per-player solve timer. Ticks only while focused; emits timer_update
        // every 5 s so other players see our time advance.
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
    }

    public void OnHudWired()
    {
        // Coop doesn't expose a per-player retry — session restart is server-controlled.
        var retryBtn = _controller.HudRetryButton;
        if (retryBtn != null)
            retryBtn.style.display = DisplayStyle.None;

        // No solo timer in coop — hide the shared timer label. (Classic
        // builds a GameTimerView over the same label in its OnHudWired.)
        var hudRoot = _controller.HudDocument?.rootVisualElement;
        var timerLabel = hudRoot?.Q<Label>("timer-label");
        if (timerLabel != null)
            timerLabel.style.display = DisplayStyle.None;
    }

    public Func<Cell, Vector3, bool> TapAttemptHandler => OnCoopTap;

    public void OnArrowCleared()
    {
        // Server is authoritative on clears in coop; no client-side autosave.
    }

    public void WireRunFlow()
    {
        // Server-driven completion. CoopSession.LobbyCompleted (wired during
        // RunSetup) raises the results overlay. Nothing to wire here.
    }

    public bool SupportsSaveOnLeave => false;
    public bool WouldOverwriteDifferentSave => false;
    public bool HasInProgressChanges => false;
    public Action OnQuickSaveHandler => null;

    public void SaveAndLeave()
    {
        // Coop doesn't save; just leave. Reconnect is also halted by
        // GameController.RequestReturnToModeSelect via the existing path.
        SceneNav.Pop();
    }

    public void Dispose()
    {
        // Idempotent — GameController.ReturnToModeSelect calls this early to
        // halt the reconnect driver, then OnDestroy calls it again.
        _coopShouldReconnect = false;
        _coopResults?.Dispose();
        _coopResults = null;
        _coopSidebar?.Dispose();
        _coopSidebar = null;
        _coopPlayerTimer?.Dispose();
        _coopPlayerTimer = null;
        _coopSession?.Dispose();
        _coopSession = null;
        _coopClient?.Dispose();
        _coopClient = null;
    }

    // ---- Setup pipeline ----------------------------------------------------

    private IEnumerator RunSetup(GameContext context)
    {
        _coopLobbyCode = GameSettings.ConsumeActiveLobbyCode();
        if (string.IsNullOrEmpty(_coopLobbyCode))
        {
            Debug.LogError("[CoopMode] Setup with no active lobby code.");
            SceneNav.Pop();
            yield break;
        }

        _controller.ShowLoadingInternal("Connecting...");
        yield return null;

        var api = new ApiClient();
        if (!api.IsLoggedIn)
        {
            Debug.LogError("[CoopMode] Co-op mode requires login.");
            SceneNav.Pop();
            yield break;
        }

        // Completed-lobby path: skip the WS game-state flow and instead
        // fetch the v6 replay to render the results screen. Set by
        // CoopHubController when the user opens a Completed lobby.
        if (GameSettings.IsCompletedLobbyView)
        {
            GameSettings.IsCompletedLobbyView = false;
            yield return CoopCompletedSetup(api);
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
                        $"[CoopMode] Co-op welcome for lobby {_coopLobbyCode} (you={_coopUserId})"
                    );
                    break;
                case "gen_progress":
                    var pct = msg.Payload?.Value<int>("pct") ?? 0;
                    _controller.SetLoadProgress(pct / 100f);
                    _controller.UpdateLoadingLabelInternal($"Generating... {pct}%");
                    break;
                case "gen_complete":
                    _controller.SetLoadProgress(1f);
                    _controller.UpdateLoadingLabelInternal("Board ready!");
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

        // Refresh the access token before the WS upgrade so a stale 15-minute
        // access JWT (or cookie) isn't rejected. In cookie mode the browser
        // picks up the fresh arrow_access and attaches it to the upgrade; in
        // bearer mode we re-read api.Token after the refresh returns.
        var refreshTask = api.EnsureFreshTokenAsync();
        while (!refreshTask.IsCompleted)
            yield return null;

        var connectTask = _coopClient.ConnectAsync(api.BaseWsUrl, _coopLobbyCode, api.Token);
        while (!connectTask.IsCompleted)
            yield return null;
        if (connectTask.IsFaulted)
        {
            Debug.LogError($"[CoopMode] Co-op connect failed: {connectTask.Exception}");
            SceneNav.Pop();
            yield break;
        }

        var helloTask = _coopClient.SendAsync(CoopMessage.Hello(0));
        while (!helloTask.IsCompleted)
            yield return null;
        if (helloTask.IsFaulted)
        {
            Debug.LogError($"[CoopMode] Co-op hello failed: {helloTask.Exception}");
            SceneNav.Pop();
            yield break;
        }

        // Wait for snapshot binary frame (or failure).
        while (_coopSnapshotData == null && !failed)
        {
            if (_controller.CancelRequested)
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
            Debug.LogWarning($"[CoopMode] Co-op failed: {failReason}");
            _controller.UpdateLoadingLabelInternal(failReason ?? "Failed");
            // Wait a beat so the user sees the message, then pop.
            yield return new WaitForSeconds(2f);
            SceneNav.Pop();
            yield break;
        }

        // Decode snapshot and build the board.
        _controller.UpdateLoadingLabelInternal("Loading board...");
        yield return null;

        Board decodedBoard = null;
        try
        {
            decodedBoard = BinarySnapshot.DecodeFull(_coopSnapshotData);
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoopMode] Failed to decode co-op snapshot: {e}");
        }
        if (decodedBoard == null)
        {
            SceneNav.Pop();
            yield break;
        }
        var board = decodedBoard;

        Debug.Log(
            $"[CoopMode] Co-op board decoded: {board.Width}x{board.Height}, {board.Arrows.Count} arrows"
        );

        // Create the view from the already-populated board. Wrapped so a
        // rendering failure (missing shader, etc.) doesn't strand the user on
        // the loading overlay with a dead coroutine.
        var vs = ThemeManager.Current ?? context.VisualSettings;
        BoardView boardView = null;
        CameraController camCtrl = null;
        bool viewInitFailed = false;
        try
        {
            var boardGo = new GameObject("BoardView");
            boardView = boardGo.AddComponent<BoardView>();
            boardView.Init(board, vs, spawnArrows: true);

            if (context.MainCamera != null)
            {
                float? zoom = GameSettings.IsSet
                    ? PlayerPrefs.GetFloat(
                        GameSettings.ZoomSpeedPrefKey,
                        GameSettings.DefaultZoomSpeed
                    )
                    : (float?)null;
                camCtrl = BoardSetupHelper.SetupCamera(context.MainCamera, board, zoom);
            }
            boardView.SetCameraController(camCtrl);
            boardView.ApplyColoring();
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoopMode] Co-op board view init failed: {e}");
            _controller.UpdateLoadingLabelInternal("Render failed");
            viewInitFailed = true;
        }
        if (viewInitFailed)
        {
            yield return new WaitForSeconds(2f);
            SceneNav.Pop();
            yield break;
        }

        context.Board = board;
        context.BoardView = boardView;
        context.CameraController = camCtrl;

        _controller.HideLoadingInternal();

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
            {
                var input = _controller.ActiveInputHandler;
                return input != null ? input.LastInputTimeUtc : DateTime.UtcNow;
            }
        );

        // Create the session wrapper + wire server event handlers.
        _coopSession = new CoopSession(_coopClient, board, _coopUserId);
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
        var hudRoot = _controller.HudDocument?.rootVisualElement;
        if (hudRoot != null)
            _coopSidebar = new CoopSidebar(_coopSession, hudRoot);
    }

    // ---- Completed-lobby read-only path ------------------------------------

    /// <summary>
    /// Completed-lobby path: fetch the v6 replay once, synthesize a roster
    /// from its header, and render the results overlay. Skips all the
    /// board/camera/input/WS plumbing — this view is read-only and static.
    /// </summary>
    private IEnumerator CoopCompletedSetup(ApiClient api)
    {
        var fetchTask = api.GetLobbyReplayAsync(_coopLobbyCode);
        while (!fetchTask.IsCompleted)
            yield return null;

        var result = fetchTask.Result;
        if (!result.Success || result.Data == null)
        {
            _controller.HideLoadingInternal();
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError(result.Error ?? "Replay unavailable");
            SceneNav.Pop();
            yield break;
        }

        var replay = result.Data;
        var roster = new Dictionary<Guid, CoopPlayer>();

        // Count per-player clears from events.
        var countsByPlayer = new Dictionary<Guid, int>();
        if (replay.events != null)
        {
            foreach (var evt in replay.events)
            {
                if (evt.type == ReplayEventType.Clear && evt.playerId != null)
                {
                    countsByPlayer.TryGetValue(evt.playerId.Value, out int c);
                    countsByPlayer[evt.playerId.Value] = c + 1;
                }
            }
        }

        if (replay.roster != null)
        {
            foreach (var entry in replay.roster)
            {
                countsByPlayer.TryGetValue(entry.playerId, out int clears);
                var color = ParseHex(entry.color);
                roster[entry.playerId] = new CoopPlayer(
                    entry.playerId,
                    entry.displayName,
                    color,
                    clears,
                    accumulatedMillis: 0,
                    online: false,
                    isLocal: entry.playerId == _coopUserId
                );
            }
        }

        // Best-effort: identify the local player. The replay endpoint
        // requires registration, so we expect one of the roster entries
        // to be us — match against the display name saved in PlayerPrefs
        // since we don't have a guaranteed user-id source here.
        if (_coopUserId == Guid.Empty && replay.roster != null)
        {
            foreach (var entry in replay.roster)
            {
                if (entry.displayName == GameSettings.DisplayName)
                {
                    _coopUserId = entry.playerId;
                    if (roster.TryGetValue(entry.playerId, out var p))
                        roster[entry.playerId] = p.With();
                    break;
                }
            }
        }

        _controller.HideLoadingInternal();

        var hudRoot = _controller.HudDocument?.rootVisualElement;
        if (hudRoot != null)
        {
            HideGameplayHudForResults();
            _coopResults = new CoopResultsScreen(
                hudRoot,
                roster,
                _coopUserId,
                _coopLobbyCode,
                onViewReplay: () =>
                {
                    GameSettings.StartReplay(replay);
                    SceneNav.Push("ReplayViewer");
                },
                selfAccumulatedMillisOverride: _coopPlayerTimer?.AccumulatedMillis ?? 0
            );
            _coopResults.Show();
        }
    }

    private static Color32 ParseHex(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return new Color32(255, 255, 255, 255);
        var s = hex.StartsWith("#") ? hex.Substring(1) : hex;
        if (s.Length != 6)
            return new Color32(255, 255, 255, 255);
        try
        {
            return new Color32(
                Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16),
                255
            );
        }
        catch
        {
            return new Color32(255, 255, 255, 255);
        }
    }

    // ---- Session event handlers --------------------------------------------

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
        var boardView = _controller.BoardViewRef;
        if (evt.Arrow == null || boardView == null)
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
        boardView.ClearArrowAnimated(evt.Arrow, flashColor: tint);
        if (_coopTapPool != null)
            _coopTapPool.Spawn(evt.TapWorld, tint);
    }

    private void OnCoopRemoteRejectedDep(CoopSession.RejectedDepEvent evt)
    {
        // Server broadcast a dep rejection. Play the standard blocked-tap
        // flash on that arrow for all players. If it was OUR attempt, the
        // flash doubles as a rollback signal since we hadn't actually
        // removed the arrow locally (TrySubmitClear only reads it).
        var boardView = _controller.BoardViewRef;
        if (evt.Arrow != null && boardView != null)
        {
            // TryClearArrow plays the reject animation if the arrow isn't clearable.
            boardView.TryClearArrow(evt.Arrow);
        }
    }

    private void OnCoopLobbyCompleted()
    {
        var input = _controller.ActiveInputHandler;
        if (input != null)
            input.SetInputEnabled(false);
        if (GlobalToast.Instance != null)
            GlobalToast.Instance.ShowInfo("Board cleared!");
        ShowCoopResultsOverlay();
    }

    private void ShowCoopResultsOverlay()
    {
        if (_coopResults != null || _coopSession == null)
            return;
        var hudRoot = _controller.HudDocument?.rootVisualElement;
        if (hudRoot == null)
            return;
        HideGameplayHudForResults();
        _coopResults = new CoopResultsScreen(
            hudRoot,
            _coopSession.Roster,
            _coopUserId,
            _coopLobbyCode,
            onViewReplay: LaunchCoopReplay,
            selfAccumulatedMillisOverride: _coopPlayerTimer?.AccumulatedMillis ?? 0
        );
        _coopResults.Show();
    }

    /// <summary>
    /// Hide HUD elements (back/retry/timer/trail buttons + co-op sidebar)
    /// before showing the results overlay so they don't bleed through behind
    /// the modal's translucent backdrop.
    /// </summary>
    private void HideGameplayHudForResults()
    {
        var root = _controller.HudDocument?.rootVisualElement;
        if (root == null)
            return;
        foreach (
            var name in new[] { "back-to-menu-btn", "retry-btn", "timer-label", "trail-toggle-btn" }
        )
        {
            var el = root.Q(name);
            if (el != null)
                el.style.display = DisplayStyle.None;
        }
        _coopSidebar?.Dispose();
        _coopSidebar = null;
    }

    private async void LaunchCoopReplay()
    {
        var api = new ApiClient();
        var result = await api.GetLobbyReplayAsync(_coopLobbyCode);
        if (!result.Success)
        {
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError(result.Error ?? "Replay unavailable");
            return;
        }
        GameSettings.StartReplay(result.Data);
        SceneNav.Push("Replay");
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

    // ---- Reconnect ---------------------------------------------------------

    private static float GetReconnectDelay(int attempt)
    {
        if (attempt < 0)
            attempt = 0;
        if (attempt >= CoopReconnectDelays.Length)
            attempt = CoopReconnectDelays.Length - 1;
        return CoopReconnectDelays[attempt];
    }

    private async Task AttemptCoopReconnectAsync()
    {
        try
        {
            // Refresh the access token before the reconnect — the original
            // 15-minute access JWT is almost certainly expired by the time a
            // reconnect attempt runs. In cookie mode this rotates the
            // arrow_access cookie the browser will attach to the WS upgrade.
            var api = new ApiClient();
            await api.EnsureFreshTokenAsync();
            await _coopClient.ReconnectAsync(api.Token);
            await _coopClient.SendAsync(CoopMessage.Hello(0));
            _coopReconnectAttempt = 0;
            _coopReconnectInFlight = false;
            if (GlobalToast.Instance != null)
                GlobalToast.Instance.ShowInfo("Reconnected");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoopMode] Co-op reconnect failed: {e.Message}");
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

    // ---- Tap submission ----------------------------------------------------

    private bool OnCoopTap(Cell cell, Vector3 tapWorld)
    {
        if (_coopSession == null)
            return true;
        var arrow = _coopSession.TrySubmitClear(cell, tapWorld);
        var boardView = _controller.BoardViewRef;
        if (arrow != null && boardView != null)
        {
            // Optimistic animation — user sees the arrow vanish immediately.
            // Server accept (`cleared`) does nothing further for our tap;
            // server reject-dep plays the reject flash; server reject-race
            // is silent (arrow was already gone).
            boardView.TryClearArrow(arrow);
        }
        return true;
    }
}
