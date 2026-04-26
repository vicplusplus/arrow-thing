using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Co-op mode: server-authoritative real-time collaboration. Board comes from
/// a server snapshot (no client-side generation), clears go through a
/// <see cref="CoopSession"/>, completion is server-driven (no client victory
/// wiring), and a roster sidebar / per-player timer run alongside the
/// gameplay HUD.
///
/// PHASE 1 of the per-mode encapsulation refactor: this MonoBehaviour
/// currently delegates back to <see cref="GameController"/> for the heavy
/// lifting (WebSocket lifecycle, snapshot decode, reconnect/heartbeat,
/// roster diff, results overlay). Subsequent phases will absorb that into
/// this class.
///
/// Eventually this class will own:
///   - <see cref="CoopClient"/>, <see cref="CoopSession"/>,
///     <see cref="CoopPlayerTimer"/>, <see cref="CoopSidebar"/>,
///     <see cref="CoopResultsScreen"/>, reconnect backoff state.
///   - <c>OnCoopRemoteCleared</c> / <c>OnCoopLobbyCompleted</c> /
///     <c>OnCoopRosterUpdated</c> handlers.
///   - <c>OnCoopTap</c> input handler.
///   - The full co-op setup coroutine (WebSocket connect → snapshot decode →
///     board view → session wiring → sidebar mount).
/// </summary>
public sealed class CoopMode : MonoBehaviour, IGameMode
{
    private GameController _controller;

    public string Name => "Coop";

    /// <summary>Wired by GameController immediately after AddComponent.</summary>
    public void Bind(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public IEnumerator Setup(GameContext context) => _controller.RunCoopSetupCoroutine(context);

    public void Tick() => _controller.UpdateCoopRuntime();

    public void OnHudWired()
    {
        // Coop doesn't expose a per-player retry — session restart is server-controlled.
        UnityEngine.UIElements.Button retryBtn = _controller.HudRetryButton;
        if (retryBtn != null)
            retryBtn.style.display = DisplayStyle.None;
    }

    public Func<Cell, Vector3, bool> TapAttemptHandler => _controller.OnCoopTapInternal;

    public void OnArrowCleared()
    {
        // Server is authoritative on clears in coop; no client-side autosave.
    }

    public void WireRunFlow()
    {
        // Server-driven completion. CoopSession.LobbyCompleted (wired during
        // CoopSetup) raises the results overlay. Nothing to wire here.
    }

    public bool SupportsSaveOnLeave => false; // Coop: no client-side save.

    public bool WouldOverwriteDifferentSave => false;

    public bool HasInProgressChanges => false;

    public Action OnQuickSaveHandler => null;

    public void SaveAndLeave()
    {
        // Coop doesn't save; just leave. Reconnect is also halted by
        // GameController.ReturnToModeSelect via the existing path.
        SceneNav.Pop();
    }

    public void Dispose()
    {
        // Coop WebSocket / session disposal still happens in GameController.OnDestroy
        // pending the full extraction (see class docs).
    }
}
