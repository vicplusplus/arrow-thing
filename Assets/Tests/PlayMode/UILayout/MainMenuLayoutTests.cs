using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

[TestFixture]
public class MainMenuLayoutTests : UILayoutTestBase
{
    [UnityTest]
    public IEnumerator MainMenu_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(MainMenuUxmlPath, ratio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var mainMenu = root.Q("main-menu");
        var menuRoot = mainMenu.Q("menu-root");
        var panelBounds = root.worldBound;
        string ctx = $"MainMenu @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            menuRoot,
            panelBounds,
            ctx,
            warn,
            menuRoot.Q(className: "title"),
            menuRoot.Q<Button>("play-btn"),
            menuRoot.Q<Button>("settings-btn"),
            menuRoot.Q<Button>("quit-btn"),
            menuRoot.Q<Button>("link-github-btn"),
            menuRoot.Q<Button>("link-discord-btn")
        );
    }

    [UnityTest]
    public IEnumerator MainMenu_Play_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(MainMenuUxmlPath, ratio);

        // Switch to play sub-menu
        root.Q("menu-root").AddToClassList("screen--hidden");
        root.Q("menu-play").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var play = root.Q("menu-play");
        var panelBounds = root.worldBound;
        string ctx = $"MainMenu_Play @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            play,
            panelBounds,
            ctx,
            warn,
            play.Q(className: "title"),
            play.Q<Button>("back-play-btn"),
            play.Q<Button>("singleplayer-btn"),
            play.Q<Button>("multiplayer-btn")
        );
    }

    [UnityTest]
    public IEnumerator MainMenu_Singleplayer_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(MainMenuUxmlPath, ratio);

        root.Q("menu-root").AddToClassList("screen--hidden");
        root.Q("menu-singleplayer").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var sp = root.Q("menu-singleplayer");
        var panelBounds = root.worldBound;
        string ctx = $"MainMenu_Singleplayer @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            sp,
            panelBounds,
            ctx,
            warn,
            sp.Q<Label>(className: "section-label"),
            sp.Q<Button>("back-sp-btn"),
            sp.Q<Button>("leaderboard-btn"),
            sp.Q<Button>("preset-small"),
            sp.Q<Button>("preset-medium"),
            sp.Q<Button>("preset-large"),
            sp.Q<Button>("preset-xlarge"),
            sp.Q<Button>("preset-custom"),
            sp.Q<Button>("start-btn")
        );
    }

    [UnityTest]
    public IEnumerator MainMenu_Singleplayer_WithSave_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(MainMenuUxmlPath, ratio);

        root.Q("menu-root").AddToClassList("screen--hidden");
        root.Q("menu-singleplayer").RemoveFromClassList("screen--hidden");
        root.Q<Button>("continue-btn").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var sp = root.Q("menu-singleplayer");
        var panelBounds = root.worldBound;
        string ctx = $"MainMenu_Singleplayer_WithSave @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            sp,
            panelBounds,
            ctx,
            warn,
            sp.Q<Button>("start-btn"),
            sp.Q<Button>("continue-btn")
        );
    }

    [UnityTest]
    public IEnumerator MainMenu_Multiplayer_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(MainMenuUxmlPath, ratio);

        root.Q("menu-root").AddToClassList("screen--hidden");
        root.Q("menu-multiplayer").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var mp = root.Q("menu-multiplayer");
        var panelBounds = root.worldBound;
        string ctx = $"MainMenu_Multiplayer @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            mp,
            panelBounds,
            ctx,
            warn,
            mp.Q(className: "title"),
            mp.Q<Button>("back-mp-btn"),
            mp.Q<Button>("coop-btn")
        );
    }

    [UnityTest]
    public IEnumerator Settings_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(SettingsPanelUxmlPath, ratio);

        root.Q("settings").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var settings = root.Q("settings");
        var panelBounds = root.worldBound;
        string ctx = $"Settings @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            settings,
            panelBounds,
            ctx,
            warn,
            settings.Q<Button>("nav-account"),
            settings.Q<Button>("nav-gameplay"),
            settings.Q<Button>("nav-data"),
            settings.Q<Button>("nav-about"),
            settings.Q<Button>("settings-close-btn")
        );
    }

    [UnityTest]
    public IEnumerator QuitModal_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(MainMenuUxmlPath, ratio);

        var modal = root.Q("quit-modal");
        modal.style.display = DisplayStyle.Flex;
        var overlay = modal.Q(className: "modal-overlay");
        overlay.RemoveFromClassList("screen--hidden");

        modal.Q<Label>("modal-title").text = "Quit game?";
        modal.Q<Button>("modal-confirm-btn").text = "Yes";
        modal.Q<Button>("modal-cancel-btn").text = "No";

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var panelBounds = root.worldBound;
        string ctx = $"QuitModal @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            overlay,
            panelBounds,
            ctx,
            warn,
            modal.Q<Label>("modal-title"),
            modal.Q<Button>("modal-confirm-btn"),
            modal.Q<Button>("modal-cancel-btn")
        );
    }

    [UnityTest]
    public IEnumerator ClearScoresModal_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(SettingsPanelUxmlPath, ratio);

        var modal = root.Q("clear-scores-modal");
        modal.style.display = DisplayStyle.Flex;
        var overlay = modal.Q(className: "modal-overlay");
        overlay.RemoveFromClassList("screen--hidden");

        modal.Q<Label>("modal-title").text = "Delete all non-favorited scores?";
        var subtitle = modal.Q<Label>("modal-subtitle");
        subtitle.text = "Favorited entries will be kept.";
        subtitle.RemoveFromClassList("screen--hidden");
        modal.Q<Button>("modal-confirm-btn").text = "Delete";
        modal.Q<Button>("modal-cancel-btn").text = "Cancel";

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var panelBounds = root.worldBound;
        string ctx = $"ClearScoresModal @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            overlay,
            panelBounds,
            ctx,
            warn,
            modal.Q<Label>("modal-title"),
            subtitle,
            modal.Q<Button>("modal-confirm-btn"),
            modal.Q<Button>("modal-cancel-btn")
        );
    }

    [UnityTest]
    public IEnumerator Settings_LoginForm_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(SettingsPanelUxmlPath, ratio);

        root.Q("settings").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var settings = root.Q("settings");
        var panelBounds = root.worldBound;
        string ctx = $"Settings_Login @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        var loginForm = settings.Q("login-form");
        AssertElements(
            loginForm,
            panelBounds,
            ctx,
            warn,
            loginForm.Q<Button>("login-submit-btn"),
            loginForm.Q<Button>("register-submit-btn")
        );
    }

    [UnityTest]
    public IEnumerator Settings_ResetForm_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(SettingsPanelUxmlPath, ratio);

        root.Q("settings").RemoveFromClassList("screen--hidden");

        var settings = root.Q("settings");
        settings.Q("login-form").AddToClassList("screen--hidden");
        settings.Q("reset-form").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var panelBounds = root.worldBound;
        string ctx = $"Settings_Reset @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        var resetForm = settings.Q("reset-form");
        AssertElements(
            resetForm,
            panelBounds,
            ctx,
            warn,
            resetForm.Q<Label>("reset-message"),
            resetForm.Q<Button>("reset-submit-btn"),
            resetForm.Q<Button>("reset-back-btn")
        );
    }

    [UnityTest]
    public IEnumerator Settings_ConfirmEmailForm_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(SettingsPanelUxmlPath, ratio);

        root.Q("settings").RemoveFromClassList("screen--hidden");

        var settings = root.Q("settings");
        settings.Q("login-form").AddToClassList("screen--hidden");
        settings.Q("confirm-email-form").RemoveFromClassList("screen--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var panelBounds = root.worldBound;
        string ctx = $"Settings_ConfirmEmail @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        var confirmForm = settings.Q("confirm-email-form");
        AssertElements(
            confirmForm,
            panelBounds,
            ctx,
            warn,
            confirmForm.Q<Label>("confirm-email-message"),
            confirmForm.Q<Button>("confirm-email-submit-btn")
        );
    }
}
