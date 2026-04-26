using System;
using UnityEngine;

/// <summary>
/// Per-mode hook surface for <see cref="GameController"/>. Centralizes the
/// previously-scattered <c>if (_isCoopMode)</c> branches across <c>Update()</c>,
/// <c>WireHud()</c>, <c>WireInput()</c>, and <c>WireVictory()</c> into a single
/// dispatch point. Adding a new mode (Endless, future PvP) becomes "implement
/// this interface" instead of "edit a dozen scattered conditionals."
///
/// Scope (Option A in <c>docs/GameControllerDecompositionPlan.md</c>): captures
/// the existing branching only. Co-op WebSocket / save / replay state remains
/// owned by GameController for now and is read by strategies via
/// <c>internal</c>-marked fields. Stages B and C (extract to dedicated
/// controllers) are deferred to dedicated refactor sessions.
/// </summary>
public interface IGameModeStrategy
{
    /// <summary>Per-frame tick. Mode-specific Update logic (heartbeat, reconnect, spawn timers, etc).</summary>
    void Update();

    /// <summary>
    /// Called from <see cref="GameController"/> after the default HUD wiring
    /// runs. Mode tweaks HUD elements (e.g. co-op hides the retry button).
    /// </summary>
    void OnHudWired();

    /// <summary>
    /// Optional tap-attempt handler injected into <see cref="InputHandler"/>.
    /// Returns null to use the default tap handling. Co-op returns its remote-
    /// broadcast handler; classic and endless return null.
    /// </summary>
    Func<Cell, Vector3, bool> TapAttemptHandler { get; }

    /// <summary>
    /// Wires the end-of-run flow. Classic wires <see cref="VictoryController"/>;
    /// co-op skips (server-driven completion); endless wires the topout result
    /// screen (deferred to phase 4).
    /// </summary>
    void WireVictory();

    /// <summary>
    /// Mode-specific cleanup. Called from <see cref="GameController.OnDestroy"/>.
    /// Co-op WebSocket teardown still lives on GameController for now (stage C2
    /// moves it here); this hook is reserved for endless-session disposal and
    /// future per-mode resources.
    /// </summary>
    void Dispose();
}
