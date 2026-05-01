using System.Collections.Generic;
using UnityEngine.UIElements;

/// <summary>
/// Orchestrates the main menu's nested screens. Each <see cref="MenuState"/>
/// maps to a self-managing <see cref="MenuScreen"/> that owns its own buttons,
/// nav graph, cancel handling, and per-frame logic. This controller dispatches
/// lifecycle calls to the active screen and persists the current state across
/// scene reloads (so returning from Game / Leaderboard lands the player back
/// in the sub-menu they came from).
/// </summary>
public sealed class MainMenuController : NavigableScene
{
    public enum MenuState
    {
        Root,
        Play,
        Singleplayer,
        Multiplayer,
    }

    private static MenuState _persistedState = MenuState.Root;

    private Dictionary<MenuState, MenuScreen> _screens;

    public MenuState CurrentState { get; private set; } = MenuState.Root;

    /// <summary>Public accessor so screens can drive direct nav-link wiring.</summary>
    public new FocusNavigator Navigator => base.Navigator;

    /// <summary>Public accessor so screens can query the document root.</summary>
    public new VisualElement Root => base.Root;

    protected override KeybindManager.Context NavContext => KeybindManager.Context.MainMenu;

    protected override void OnEnable()
    {
        // Deep-link resolution must happen before base.OnEnable calls BuildUI/SetState.
        CoopDeepLink.TryResolve();
        base.OnEnable();

        // If a lobby code was provided via deep-link, jump straight to the hub.
        if (!string.IsNullOrEmpty(GameSettings.PendingLobbyCode))
            SceneNav.Push("CoopHub");
    }

    private void Awake()
    {
        _screens = new Dictionary<MenuState, MenuScreen>
        {
            { MenuState.Root, new RootScreen(this) },
            { MenuState.Play, new PlayScreen(this) },
            { MenuState.Singleplayer, new SingleplayerScreen(this) },
            { MenuState.Multiplayer, new MultiplayerScreen(this) },
        };
    }

    protected override void BuildUI(VisualElement root)
    {
        foreach (var screen in _screens.Values)
            screen.Build(root);

        SetState(_persistedState);
    }

    protected override void BuildNavGraph(FocusNavigator nav) =>
        _screens[CurrentState].BuildNavGraph(nav);

    protected override void OnUpdate(KeybindManager km) => _screens[CurrentState].OnUpdate(km);

    protected override void OnCancel() => _screens[CurrentState].OnCancel();

    /// <summary>Switch to a different sub-menu. Re-toggles visibility and rebuilds the nav graph.</summary>
    public void SetState(MenuState state)
    {
        CurrentState = state;
        _persistedState = state;
        foreach (var kvp in _screens)
            kvp.Value.SetVisible(kvp.Key == state);
        RebuildNavigator(false);
    }

    /// <summary>Public so screens can request a rebuild after dynamic UI changes.</summary>
    public new void RebuildNavigator(bool preserveFocus) => base.RebuildNavigator(preserveFocus);
}
