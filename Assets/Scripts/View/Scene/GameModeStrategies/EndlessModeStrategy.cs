using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Endless mode strategy: instantiates an <see cref="EndlessModeController"/>
/// against the current board+view, routes player clears through it (so the
/// session's native generation state stays in sync with the played board),
/// hides the in-game retry button (the result screen owns retry once the run
/// ends), and wires the topout event to end the run.
///
/// The controller drives its own <c>Update</c> as a sibling MonoBehaviour, so
/// <see cref="Update"/> here is a no-op.
/// </summary>
public sealed class EndlessModeStrategy : IGameModeStrategy
{
    private readonly GameController _controller;
    private EndlessModeController _endless;

    public EndlessModeStrategy(GameController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        // Construct the controller eagerly: it must exist before WireHud /
        // WireInput run so OnHudWired can hide the retry button and so the
        // BoardView clear-routing is in place before the first input frame.
        _endless = _controller.gameObject.AddComponent<EndlessModeController>();
        VisualElement hudRoot =
            _controller.HudDocument != null ? _controller.HudDocument.rootVisualElement : null;

        // Inject endless-mode-specific HUD overlay (garbage meter +
        // cleared-count label) into the shared HUD root. Owned entirely
        // by endless — classic and co-op never see these elements.
        if (hudRoot != null && _controller.EndlessHudAsset != null)
            _controller.EndlessHudAsset.CloneTree(hudRoot);

        _endless.Initialize(
            _controller.CurrentBoard,
            _controller.CurrentBoardView,
            _controller.MaxArrowLength,
            spawnSeed: _controller.ActiveSeed,
            hudRoot: hudRoot,
            camera: _controller.MainCamera
        );

        // Route real-arrow clears through the session so its NativeGenerationState
        // (used for ghost cycle detection) sees the cleared arrow vanish.
        _controller.CurrentBoardView.SetArrowRemover(_endless.HandleRealArrowCleared);
    }

    public void Update()
    {
        // EndlessModeController has its own Update (preview→commit pipeline).
        // Nothing to forward here.
    }

    public void OnHudWired()
    {
        // Result screen owns retry once the run ends; no in-game retry.
        Button retryBtn = _controller.HudRetryButton;
        if (retryBtn != null)
            retryBtn.style.display = DisplayStyle.None;

        // Hide the classic timer label — endless owns its own
        // cleared-count label in EndlessHud.uxml. (Mode-specific decision;
        // GameTimerView still ticks but its label stays invisible.)
        Label timerLabel = _controller.HudTimerLabel;
        if (timerLabel != null)
            timerLabel.style.display = DisplayStyle.None;
    }

    public Func<Cell, Vector3, bool> TapAttemptHandler => null;

    public void WireVictory()
    {
        // No win-on-empty for endless. Topout fires the end-of-run sequence
        // in two beats: ToppedOut immediately disables gameplay HUD/input so
        // the player can't keep tapping during the pause; ResultReady fires
        // after a short delay (EndlessModeController.topoutResultDelaySeconds)
        // so the player can see the final board state before the modal.
        _endless.ToppedOut += OnToppedOut;
        _endless.ResultReady += OnResultReady;
    }

    public void Dispose()
    {
        if (_endless != null)
        {
            _endless.ToppedOut -= OnToppedOut;
            _endless.ResultReady -= OnResultReady;
        }
    }

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
        UIDocument victoryDoc = _controller.VictoryDocument;
        if (victoryDoc == null)
        {
            // Fallback: no result overlay available, just return to menu.
            SceneNav.Pop();
            return;
        }

        var result = _controller.gameObject.AddComponent<EndlessResultController>();
        result.Init(
            victoryDoc,
            _endless.ClearCount,
            _endless.LongestCombo,
            _endless.RunDurationSeconds
        );
    }
}
