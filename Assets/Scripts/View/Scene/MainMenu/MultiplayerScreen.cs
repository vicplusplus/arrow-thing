using UnityEngine;
using UnityEngine.UIElements;

internal sealed class MultiplayerScreen : MenuScreen
{
    private NavGraph _navGraph;

    public MultiplayerScreen(MainMenuController owner)
        : base(owner) { }

    protected override string RootElementName => "menu-multiplayer";

    protected override void BuildInternal(VisualElement documentRoot)
    {
        documentRoot.Q<Button>("coop-btn").clicked += () => SceneNav.Push("CoopHub");
        documentRoot.Q<Button>("back-mp-btn").clicked += () =>
            Owner.SetState(MainMenuController.MenuState.Play);
    }

    public override void BuildNavGraph(FocusNavigator nav)
    {
        if (_navGraph == null)
            _navGraph = Resources.Load<NavGraph>("NavGraphs/MainMenuMultiplayer");

        new NavGraphBuilder(_navGraph)
            .Bind(
                "Back",
                Owner.Root.Q<Button>("back-mp-btn"),
                onActivate: () =>
                {
                    Owner.SetState(MainMenuController.MenuState.Play);
                    return true;
                }
            )
            .Bind(
                "Coop",
                Owner.Root.Q<Button>("coop-btn"),
                onActivate: () =>
                {
                    SceneNav.Push("CoopHub");
                    return true;
                }
            )
            .Apply(nav);
    }

    public override void OnCancel() => Owner.SetState(MainMenuController.MenuState.Play);
}
