using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Shared dependencies passed from <see cref="GameController"/> to the active
/// <see cref="IGameMode"/>. Keeps the mode interface clean (no constructor
/// arg explosion, no GameController back-references for trivial accessors).
///
/// Mode mutates the <see cref="Board"/>/<see cref="BoardView"/>/<see cref="CameraController"/>/<see cref="InputHandler"/>
/// fields during <see cref="IGameMode.Setup"/> so that subsequent shared
/// wiring (HUD button connections, theme application) can read them.
/// </summary>
public sealed class GameContext
{
    // ---- Shared scene services (set by GameController, read by mode) ----

    public GameController Controller { get; }
    public Camera MainCamera { get; }
    public VisualSettings VisualSettings { get; }
    public UIDocument HudDocument { get; }

    /// <summary>
    /// Resolved board parameters (from <c>GameSettings</c> or editor
    /// overrides). Mode may use these to size board generation; coop ignores
    /// them since the server snapshot dictates dimensions.
    /// </summary>
    public int Width { get; set; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; set; }

    /// <inheritdoc cref="Width"/>
    public int MaxArrowLength { get; set; }

    /// <inheritdoc cref="Width"/>
    public int Seed { get; set; }

    // ---- Mode populates these during Setup ----

    /// <summary>Created by mode during <see cref="IGameMode.Setup"/>.</summary>
    public Board Board { get; set; }

    /// <inheritdoc cref="Board"/>
    public BoardView BoardView { get; set; }

    /// <inheritdoc cref="Board"/>
    public CameraController CameraController { get; set; }

    /// <inheritdoc cref="Board"/>
    public InputHandler InputHandler { get; set; }

    // ---- Loading-progress callback (mode → host) ----

    /// <summary>
    /// Push 0..1 to update the shared loading bar fill during setup. Host
    /// owns the bar widget; mode just reports progress.
    /// </summary>
    public Action<float, string> ReportLoadProgress { get; }

    /// <summary>
    /// Returns true if the player pressed Cancel during loading. Mode's
    /// Setup coroutine should poll this and bail out cleanly.
    /// </summary>
    public Func<bool> IsCancelRequested { get; }

    public GameContext(
        GameController controller,
        Camera mainCamera,
        VisualSettings visualSettings,
        UIDocument hudDocument,
        Action<float, string> reportLoadProgress,
        Func<bool> isCancelRequested
    )
    {
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        MainCamera = mainCamera;
        VisualSettings = visualSettings;
        HudDocument = hudDocument;
        ReportLoadProgress = reportLoadProgress ?? ((_, _) => { });
        IsCancelRequested = isCancelRequested ?? (() => false);
    }
}
