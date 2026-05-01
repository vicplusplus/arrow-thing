using UnityEngine;
using UnityEngine.UIElements;

internal sealed class PlayScreen : MenuScreen
{
    private NavGraph _navGraph;

    public PlayScreen(MainMenuController owner)
        : base(owner) { }

    protected override string RootElementName => "menu-play";

    protected override void BuildInternal(VisualElement documentRoot)
    {
        documentRoot.Q<Button>("singleplayer-btn").clicked += () =>
            Owner.SetState(MainMenuController.MenuState.Singleplayer);
        documentRoot.Q<Button>("multiplayer-btn").clicked += () =>
            Owner.SetState(MainMenuController.MenuState.Multiplayer);
        documentRoot.Q<Button>("back-play-btn").clicked += () =>
            Owner.SetState(MainMenuController.MenuState.Root);
    }

    public override void BuildNavGraph(FocusNavigator nav)
    {
        if (_navGraph == null)
            _navGraph = Resources.Load<NavGraph>("NavGraphs/MainMenuPlay");

        new NavGraphBuilder(_navGraph)
            .Bind(
                "Back",
                Owner.Root.Q<Button>("back-play-btn"),
                onActivate: () =>
                {
                    Owner.SetState(MainMenuController.MenuState.Root);
                    return true;
                }
            )
            .Bind(
                "Singleplayer",
                Owner.Root.Q<Button>("singleplayer-btn"),
                onActivate: () =>
                {
                    Owner.SetState(MainMenuController.MenuState.Singleplayer);
                    return true;
                }
            )
            .Bind(
                "Multiplayer",
                Owner.Root.Q<Button>("multiplayer-btn"),
                onActivate: () =>
                {
                    Owner.SetState(MainMenuController.MenuState.Multiplayer);
                    return true;
                }
            )
            .Apply(nav);
    }

    public override void OnCancel() => Owner.SetState(MainMenuController.MenuState.Root);
}
