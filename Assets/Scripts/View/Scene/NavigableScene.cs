using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Base class for scene controllers that use FocusNavigator and follow the
/// standard OnEnable/Update/OnDisable lifecycle. Handles UIDocument root
/// retrieval, navigator creation/disposal, ActiveContext setting, Update
/// guards, and cancel handling.
///
/// Subclasses override:
///   NavContext          — which KeybindManager.Context to activate
///   BuildUI(root)       — wire buttons, create components (called in OnEnable)
///   BuildNavGraph(nav)  — set items and links on the navigator
///   PreUpdate(km)       — return false to skip Navigator.Update() this frame (optional)
///   OnUpdate(km)        — per-frame keybinds beyond navigation (optional)
///   OnCancel()          — escape pressed after all guards pass (optional)
/// </summary>
public abstract class NavigableScene : MonoBehaviour
{
    [SerializeField]
    protected UIDocument uiDocument;

    /// <summary>The current FocusNavigator. Subclasses may reassign (e.g. dynamic list rebuild).</summary>
    protected FocusNavigator Navigator { get; set; }

    /// <summary>The UIDocument root visual element, set in OnEnable.</summary>
    protected VisualElement Root { get; private set; }

    // -- Subclass hooks -------------------------------------------------------

    /// <summary>Which keybind context to activate when this scene is enabled.</summary>
    protected abstract KeybindManager.Context NavContext { get; }

    /// <summary>Wire buttons, create UI components. Called in OnEnable.</summary>
    protected abstract void BuildUI(VisualElement root);

    /// <summary>
    /// Set up FocusNavigator items and nav graph links.
    /// Called after BuildUI. The navigator is already created.
    /// </summary>
    protected abstract void BuildNavGraph(FocusNavigator nav);

    /// <summary>
    /// Called before Navigator.Update() each frame (after guards pass).
    /// Return false to skip Navigator.Update() this frame (e.g. when a
    /// sub-navigator like a context menu is handling input).
    /// </summary>
    protected virtual bool PreUpdate(KeybindManager km) => true;

    /// <summary>Per-frame logic for scene-specific keybinds. Called after navigator.Update().</summary>
    protected virtual void OnUpdate(KeybindManager km) { }

    /// <summary>
    /// Called when Cancel is pressed and all guards pass (navigator not consuming,
    /// settings not just closed). Override for back navigation or quit confirmation.
    /// </summary>
    protected virtual void OnCancel() { }

    // -- Lifecycle ------------------------------------------------------------

    protected virtual void OnEnable()
    {
        Root = uiDocument.rootVisualElement;

        BuildUI(Root);

        Navigator?.Dispose();
        Navigator = new FocusNavigator(Root);
        BuildNavGraph(Navigator);

        if (KeybindManager.Instance != null)
            KeybindManager.Instance.ActiveContext = NavContext;
    }

    protected virtual void Update()
    {
        if (Navigator == null)
            return;

        var km = KeybindManager.Instance;
        if (km == null)
            return;

        if (SettingsController.Instance != null && SettingsController.Instance.IsOpen)
            return;

        if (PreUpdate(km))
            Navigator.Update();

        OnUpdate(km);

        if (
            km.Cancel.WasPerformedThisFrame()
            && !Navigator.ConsumesCancel
            && !SettingsController.JustClosed
        )
        {
            OnCancel();
        }
    }

    // -- Helpers --------------------------------------------------------------

    /// <summary>
    /// Rebuild the navigator in-place (e.g. after dynamic UI changes like
    /// showing/hiding custom sliders). Preserves focus index and ring state
    /// when <paramref name="preserveFocus"/> is true.
    /// </summary>
    protected void RebuildNavigator(bool preserveFocus = false)
    {
        int savedIndex = Navigator?.CurrentIndex ?? 0;
        bool savedRing = Navigator != null && Navigator.IsRingVisible;

        Navigator?.Dispose();
        Navigator = new FocusNavigator(Root);
        BuildNavGraph(Navigator);

        if (preserveFocus && Navigator != null && savedRing)
            Navigator.SetFocus(savedIndex);
    }

    /// <summary>
    /// Check whether Cancel should be processed this frame. Returns true if
    /// Cancel was pressed and no guards are blocking. Use in controllers that
    /// don't inherit NavigableScene (e.g. GameController, ReplayViewController).
    /// </summary>
    public static bool ShouldHandleCancel(FocusNavigator nav)
    {
        var km = KeybindManager.Instance;
        if (km == null)
            return false;
        if (!km.Cancel.WasPerformedThisFrame())
            return false;
        if (nav != null && nav.ConsumesCancel)
            return false;
        if (SettingsController.Instance != null && SettingsController.Instance.IsOpen)
            return false;
        if (SettingsController.JustClosed)
            return false;
        return true;
    }
}
