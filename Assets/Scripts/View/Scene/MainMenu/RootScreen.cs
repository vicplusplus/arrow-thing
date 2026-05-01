using UnityEngine;
using UnityEngine.UIElements;

internal sealed class RootScreen : MenuScreen
{
    private NavGraph _navGraph;
    private ConfirmModal _quitModal;
    private Button _quitBtn;

    public RootScreen(MainMenuController owner)
        : base(owner) { }

    protected override string RootElementName => "menu-root";

    protected override void BuildInternal(VisualElement documentRoot)
    {
        documentRoot.Q<Button>("play-btn").clicked += () =>
            Owner.SetState(MainMenuController.MenuState.Play);
        documentRoot.Q<Button>("settings-btn").clicked += () => SettingsController.Instance.Open();
        documentRoot.Q<Button>("link-github-btn").clicked += () =>
            ExternalLinks.Open(ExternalLinks.GitHub);
        documentRoot.Q<Button>("link-discord-btn").clicked += () =>
            ExternalLinks.Open(ExternalLinks.Discord);

        _quitBtn = documentRoot.Q<Button>("quit-btn");
        if (HasQuit)
            _quitBtn.clicked += ShowQuitModal;
        else
            _quitBtn.style.display = DisplayStyle.None;

        _quitModal = new ConfirmModal(documentRoot.Q("quit-modal"), "Quit game?", "Yes", "No");
        _quitModal.Confirmed += OnQuitConfirm;
        _quitModal.Cancelled += () => _quitModal.Hide();
    }

    public override void BuildNavGraph(FocusNavigator nav)
    {
        if (_navGraph == null)
            _navGraph = Resources.Load<NavGraph>("NavGraphs/MainMenuRoot");

        new NavGraphBuilder(_navGraph)
            .Bind(
                "Quit",
                HasQuit ? _quitBtn : null,
                onActivate: () =>
                {
                    ShowQuitModal();
                    return true;
                }
            )
            .Bind(
                "Play",
                Owner.Root.Q<Button>("play-btn"),
                onActivate: () =>
                {
                    Owner.SetState(MainMenuController.MenuState.Play);
                    return true;
                }
            )
            .Bind(
                "Settings",
                Owner.Root.Q<Button>("settings-btn"),
                onActivate: () =>
                {
                    SettingsController.Instance.Open();
                    return true;
                }
            )
            .Bind(
                "GitHub",
                Owner.Root.Q<Button>("link-github-btn"),
                onActivate: () =>
                {
                    ExternalLinks.Open(ExternalLinks.GitHub);
                    return true;
                }
            )
            .Bind(
                "Discord",
                Owner.Root.Q<Button>("link-discord-btn"),
                onActivate: () =>
                {
                    ExternalLinks.Open(ExternalLinks.Discord);
                    return true;
                }
            )
            .Apply(nav);
    }

    public override void OnCancel()
    {
        if (HasQuit)
            ShowQuitModal();
    }

    private static bool HasQuit =>
        !Application.isMobilePlatform && Application.platform != RuntimePlatform.WebGLPlayer;

    private void ShowQuitModal() => _quitModal.Show();

    private static void OnQuitConfirm()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
