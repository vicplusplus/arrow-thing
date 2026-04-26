using System;
using UnityEngine;

/// <summary>
/// Default singleplayer flow: fixed board, win-on-empty, save/replay enabled.
/// All hooks are no-ops except <see cref="WireVictory"/>, which delegates to
/// <see cref="GameController"/>'s existing victory-wiring helper (preserves
/// existing behavior verbatim).
/// </summary>
public sealed class ClassicModeStrategy : IGameModeStrategy
{
    private readonly GameController _controller;

    public ClassicModeStrategy(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public void Update() { }

    public void OnHudWired() { }

    public Func<Cell, Vector3, bool> TapAttemptHandler => null;

    public void WireVictory()
    {
        _controller.WireVictoryDefault();
    }

    public void Dispose() { }
}
