using UnityEngine.UIElements;

/// <summary>
/// Base class for one logical screen inside <see cref="MainMenuController"/>.
/// Each screen owns its own root VisualElement, button wiring, and nav graph.
/// The controller is responsible for showing/hiding screens and dispatching
/// lifecycle calls (BuildNavGraph / OnUpdate / OnCancel) to the active screen.
/// </summary>
internal abstract class MenuScreen
{
    protected readonly MainMenuController Owner;

    /// <summary>The screen's root element inside the document. Set during <see cref="Build"/>.</summary>
    public VisualElement Root { get; private set; }

    protected MenuScreen(MainMenuController owner)
    {
        Owner = owner;
    }

    /// <summary>Name of the screen's root element in the UXML document.</summary>
    protected abstract string RootElementName { get; }

    /// <summary>Wire button handlers and cache child elements. Called once per scene activation.</summary>
    public void Build(VisualElement documentRoot)
    {
        Root = documentRoot.Q(RootElementName);
        BuildInternal(documentRoot);
    }

    protected abstract void BuildInternal(VisualElement documentRoot);

    /// <summary>Build the focus nav graph for this screen onto <paramref name="nav"/>.</summary>
    public abstract void BuildNavGraph(FocusNavigator nav);

    /// <summary>Per-frame keybinds specific to this screen. Default: no-op.</summary>
    public virtual void OnUpdate(KeybindManager km) { }

    /// <summary>Cancel pressed while this screen is active. Default: no-op.</summary>
    public virtual void OnCancel() { }

    public void SetVisible(bool visible)
    {
        if (visible)
            Root.RemoveFromClassList("screen--hidden");
        else
            Root.AddToClassList("screen--hidden");
    }
}
