using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Co-op mode strategy: heartbeat / reconnect / per-player timer driven each
/// frame; tap attempts route through the co-op session for server validation;
/// retry button hidden in HUD; victory flow skipped (server emits
/// <c>lobby_completed</c>).
///
/// Reads co-op runtime state from <see cref="GameController"/> via
/// <c>internal</c> accessors. Stage C2 in
/// <c>docs/GameControllerDecompositionPlan.md</c> moves the WebSocket /
/// reconnect / session ownership here entirely, leaving GameController out of
/// the co-op picture. For Option A the state stays on GameController and this
/// strategy borrows it.
/// </summary>
public sealed class CoopModeStrategy : IGameModeStrategy
{
    private readonly GameController _controller;

    public CoopModeStrategy(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public void Update()
    {
        _controller.UpdateCoopRuntime();
    }

    public void OnHudWired()
    {
        // Co-op has no per-player retry — session restart is server-controlled.
        Button retryBtn = _controller.HudRetryButton;
        if (retryBtn != null)
            retryBtn.style.display = DisplayStyle.None;
    }

    public Func<Cell, Vector3, bool> TapAttemptHandler => _controller.OnCoopTapInternal;

    public void WireVictory()
    {
        // Server-driven completion. CoopSession.LobbyCompleted (wired during
        // CoopSetup) raises the results overlay. Nothing to wire here.
    }

    public void Dispose()
    {
        // Co-op WebSocket / session disposal still happens in GameController.OnDestroy
        // for now (see stage C2 in docs/GameControllerDecompositionPlan.md).
    }
}
