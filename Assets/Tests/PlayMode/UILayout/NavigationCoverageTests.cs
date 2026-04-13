using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

/// <summary>
/// State-based navigation coverage tests. For each scene, defines the set of
/// possible UI states and validates that:
///
/// Per state:
///   1. Every navigable button exists and is visible.
///   2. Every background button (visible but behind modal) exists and is visible.
///   3. No other visible button is uncovered.
///
/// Per scene:
///   4. Every named Button in the UXML is navigable in at least one state.
///
/// Adding a new button to UXML will fail these tests unless it is added to
/// the appropriate state's Navigable set or hidden in all states.
/// </summary>
[TestFixture]
public class NavigationCoverageTests : UILayoutTestBase
{
    private static readonly UILayoutTestHelper.AspectRatio StandardRatio =
        new UILayoutTestHelper.AspectRatio("16:9", 1920, 1080);

    // ── State definition ────────────────────────────────────────────

    public class UIState
    {
        public readonly string Name;
        public readonly Action<VisualElement> Setup;
        public readonly string[] Navigable;
        public readonly string[] Background;

        public UIState(
            string name,
            Action<VisualElement> setup,
            string[] navigable,
            string[] background = null
        )
        {
            Name = name;
            Setup = setup;
            Navigable = navigable;
            Background = background ?? Array.Empty<string>();
        }

        public override string ToString() => Name;
    }

    // ── Validation helpers ──────────────────────────────────────────

    /// <summary>
    /// Returns true if the element and all its ancestors have display != None.
    /// </summary>
    private static bool IsEffectivelyVisible(VisualElement el)
    {
        while (el != null)
        {
            if (el.resolvedStyle.display == DisplayStyle.None)
                return false;
            el = el.parent;
        }
        return true;
    }

    /// <summary>
    /// Finds all named Button elements in the tree.
    /// </summary>
    private static List<Button> FindAllNamedButtons(VisualElement root)
    {
        var buttons = new List<Button>();
        root.Query<Button>()
            .ForEach(btn =>
            {
                if (!string.IsNullOrEmpty(btn.name))
                    buttons.Add(btn);
            });
        return buttons;
    }

    /// <summary>
    /// Validates a single UI state. Checks that the set of visible buttons
    /// exactly matches the union of Navigable and Background.
    /// </summary>
    private static void AssertStateNavigation(VisualElement root, UIState state)
    {
        var accountedFor = new HashSet<string>(state.Navigable);
        foreach (string bg in state.Background)
            accountedFor.Add(bg);

        // Navigable buttons must exist and be visible. Multiple buttons may
        // share a name (e.g. modal-confirm-btn across multiple ConfirmModal
        // instances in the same UXML); at least one must be visible.
        foreach (string name in state.Navigable)
        {
            var btns = root.Query<Button>(name).ToList();
            Assert.IsNotEmpty(btns, $"[{state.Name}] Navigable button '{name}' not found in UXML");
            Assert.IsTrue(
                btns.Any(IsEffectivelyVisible),
                $"[{state.Name}] Navigable button '{name}' is hidden but should be visible"
            );
        }

        // Background buttons must exist and be visible.
        foreach (string name in state.Background)
        {
            var btns = root.Query<Button>(name).ToList();
            Assert.IsNotEmpty(btns, $"[{state.Name}] Background button '{name}' not found in UXML");
            Assert.IsTrue(
                btns.Any(IsEffectivelyVisible),
                $"[{state.Name}] Background button '{name}' is hidden but should be visible"
            );
        }

        // No visible button should be uncovered.
        var allButtons = FindAllNamedButtons(root);
        var uncovered = new List<string>();
        foreach (var btn in allButtons)
        {
            if (accountedFor.Contains(btn.name))
                continue;
            if (IsEffectivelyVisible(btn))
                uncovered.Add(btn.name);
        }

        if (uncovered.Count > 0)
        {
            Assert.Fail(
                $"[{state.Name}] Visible buttons not in Navigable or Background: "
                    + string.Join(", ", uncovered)
                    + ". Add them to the state declaration or ensure they are hidden."
            );
        }
    }

    /// <summary>
    /// Validates that every named Button in the document is navigable in at
    /// least one state. Catches buttons that are never keyboard-reachable.
    /// </summary>
    private static void AssertAllButtonsCovered(VisualElement root, string scene, UIState[] states)
    {
        var allButtons = FindAllNamedButtons(root);
        var covered = new HashSet<string>();
        foreach (var state in states)
        foreach (string name in state.Navigable)
            covered.Add(name);

        var uncovered = new List<string>();
        foreach (var btn in allButtons)
            if (!covered.Contains(btn.name))
                uncovered.Add(btn.name);

        if (uncovered.Count > 0)
        {
            Assert.Fail(
                $"[{scene}] Buttons never navigable in any state: "
                    + string.Join(", ", uncovered)
                    + ". Add a UI state that includes them or remove them from the UXML."
            );
        }
    }

    // ── MainMenu ────────────────────────────────────────────────────

    // Root-level buttons (Play, Settings, Quit, links)
    private static readonly string[] MainMenuRootButtons =
    {
        "quit-btn",
        "play-btn",
        "settings-btn",
        "link-github-btn",
        "link-discord-btn",
    };

    private static readonly UIState MainMenu_Root = new UIState(
        "MainMenu/Root",
        setup: root => { },
        navigable: MainMenuRootButtons
    );

    private static readonly UIState MainMenu_Play = new UIState(
        "MainMenu/Play",
        setup: root =>
        {
            root.Q("menu-root").AddToClassList("screen--hidden");
            root.Q("menu-play").RemoveFromClassList("screen--hidden");
        },
        navigable: new[] { "back-play-btn", "singleplayer-btn", "multiplayer-btn" }
    );

    private static readonly UIState MainMenu_Singleplayer = new UIState(
        "MainMenu/Singleplayer",
        setup: root =>
        {
            root.Q("menu-root").AddToClassList("screen--hidden");
            root.Q("menu-singleplayer").RemoveFromClassList("screen--hidden");
        },
        navigable: new[]
        {
            "back-sp-btn",
            "leaderboard-btn",
            "preset-small",
            "preset-medium",
            "preset-large",
            "preset-xlarge",
            "preset-custom",
            "start-btn",
        }
    );

    private static readonly UIState MainMenu_Singleplayer_WithContinue = new UIState(
        "MainMenu/Singleplayer+Continue",
        setup: root =>
        {
            root.Q("menu-root").AddToClassList("screen--hidden");
            root.Q("menu-singleplayer").RemoveFromClassList("screen--hidden");
            root.Q<Button>("continue-btn").RemoveFromClassList("screen--hidden");
        },
        navigable: new[]
        {
            "back-sp-btn",
            "leaderboard-btn",
            "preset-small",
            "preset-medium",
            "preset-large",
            "preset-xlarge",
            "preset-custom",
            "start-btn",
            "continue-btn",
        }
    );

    private static readonly UIState MainMenu_Multiplayer = new UIState(
        "MainMenu/Multiplayer",
        setup: root =>
        {
            root.Q("menu-root").AddToClassList("screen--hidden");
            root.Q("menu-multiplayer").RemoveFromClassList("screen--hidden");
        },
        navigable: new[] { "back-mp-btn", "coop-btn" }
    );

    private static readonly UIState MainMenu_QuitModal = new UIState(
        "MainMenu/QuitModal",
        setup: root =>
        {
            var modal = root.Q("quit-modal");
            modal.style.display = DisplayStyle.Flex;
            modal.Q(className: "modal-overlay").RemoveFromClassList("screen--hidden");
        },
        navigable: new[] { "modal-confirm-btn", "modal-cancel-btn" },
        background: MainMenuRootButtons
    );

    private static IEnumerable<UIState> MainMenuStates()
    {
        yield return MainMenu_Root;
        yield return MainMenu_Play;
        yield return MainMenu_Singleplayer;
        yield return MainMenu_Singleplayer_WithContinue;
        yield return MainMenu_Multiplayer;
        yield return MainMenu_QuitModal;
    }

    [UnityTest]
    public IEnumerator MainMenu_StateNavigation([ValueSource(nameof(MainMenuStates))] UIState state)
    {
        var root = SetUpDocument(MainMenuUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        state.Setup(root);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertStateNavigation(root, state);
    }

    [UnityTest]
    public IEnumerator MainMenu_AllButtonsCovered()
    {
        var root = SetUpDocument(MainMenuUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertAllButtonsCovered(root, "MainMenu", MainMenuStates().ToArray());
    }

    // ── GameHud ──────────────────────────────────────────────────────

    private static readonly UIState GameHud_Loading = new UIState(
        "GameHud/Loading",
        setup: root =>
        {
            // Loading overlay visible, HUD elements hidden (as GameController.ShowLoading does).
            root.Q("loading-overlay").style.display = DisplayStyle.Flex;
            root.Q("loading-overlay").style.opacity = 1f;
            root.Q<Label>("timer-label").style.display = DisplayStyle.None;
            root.Q<Button>("trail-toggle-btn").style.display = DisplayStyle.None;
            root.Q<Button>("retry-btn").style.display = DisplayStyle.None;
        },
        navigable: new[] { "back-to-menu-btn" }
    );

    private static readonly UIState GameHud_LoadingCancelModal = new UIState(
        "GameHud/Loading+CancelGenModal",
        setup: root =>
        {
            root.Q("loading-overlay").style.display = DisplayStyle.Flex;
            root.Q("loading-overlay").style.opacity = 1f;
            root.Q<Label>("timer-label").style.display = DisplayStyle.None;
            root.Q<Button>("trail-toggle-btn").style.display = DisplayStyle.None;
            root.Q<Button>("retry-btn").style.display = DisplayStyle.None;
            root.Q("cancel-generation-modal").RemoveFromClassList("modal--hidden");
        },
        navigable: new[] { "cancel-generation-yes-btn", "cancel-generation-no-btn" },
        background: new[] { "back-to-menu-btn" }
    );

    private static readonly UIState GameHud_Playing = new UIState(
        "GameHud/Playing",
        setup: root =>
        {
            // Loading overlay hidden, HUD elements visible.
            root.Q("loading-overlay").style.display = DisplayStyle.None;
        },
        navigable: new[] { "back-to-menu-btn", "retry-btn", "trail-toggle-btn" }
    );

    private static readonly UIState GameHud_PlayingLeaveModal = new UIState(
        "GameHud/Playing+LeaveModal",
        setup: root =>
        {
            root.Q("loading-overlay").style.display = DisplayStyle.None;
            var modal = root.Q("leave-modal");
            modal.style.display = DisplayStyle.Flex;
            modal.RemoveFromClassList("screen--hidden");
        },
        navigable: new[] { "modal-confirm-btn", "modal-cancel-btn" },
        background: new[] { "back-to-menu-btn", "retry-btn", "trail-toggle-btn" }
    );

    private static readonly UIState GameHud_PlayingRetryModal = new UIState(
        "GameHud/Playing+RetryModal",
        setup: root =>
        {
            root.Q("loading-overlay").style.display = DisplayStyle.None;
            var modal = root.Q("retry-modal");
            modal.style.display = DisplayStyle.Flex;
            modal.RemoveFromClassList("screen--hidden");
        },
        navigable: new[] { "modal-confirm-btn", "modal-cancel-btn" },
        background: new[] { "back-to-menu-btn", "retry-btn", "trail-toggle-btn" }
    );

    private static IEnumerable<UIState> GameHudStates()
    {
        yield return GameHud_Loading;
        yield return GameHud_LoadingCancelModal;
        yield return GameHud_Playing;
        yield return GameHud_PlayingLeaveModal;
        yield return GameHud_PlayingRetryModal;
    }

    [UnityTest]
    public IEnumerator GameHud_StateNavigation([ValueSource(nameof(GameHudStates))] UIState state)
    {
        var root = SetUpDocument(GameHudUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        state.Setup(root);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertStateNavigation(root, state);
    }

    [UnityTest]
    public IEnumerator GameHud_AllButtonsCovered()
    {
        var root = SetUpDocument(GameHudUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertAllButtonsCovered(root, "GameHud", GameHudStates().ToArray());
    }

    // ── VictoryPopup ────────────────────────────────────────────────

    private static readonly string[] VictoryButtons =
    {
        "view-leaderboard-btn",
        "play-again-btn",
        "menu-btn",
    };

    private static readonly UIState Victory_Popup = new UIState(
        "Victory/Popup",
        setup: root =>
        {
            root.Q("victory-overlay").RemoveFromClassList("victory--hidden");
        },
        navigable: VictoryButtons
    );

    private static IEnumerable<UIState> VictoryStates()
    {
        yield return Victory_Popup;
    }

    [UnityTest]
    public IEnumerator Victory_StateNavigation([ValueSource(nameof(VictoryStates))] UIState state)
    {
        var root = SetUpDocument(VictoryUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        state.Setup(root);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertStateNavigation(root, state);
    }

    [UnityTest]
    public IEnumerator Victory_AllButtonsCovered()
    {
        var root = SetUpDocument(VictoryUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertAllButtonsCovered(root, "Victory", VictoryStates().ToArray());
    }

    // ── Leaderboard ─────────────────────────────────────────────────

    private static readonly string[] LeaderboardLocalButtons =
    {
        "lb-back-btn",
        "lb-local-btn",
        "lb-global-btn",
        "tab-small",
        "tab-medium",
        "tab-large",
        "tab-xlarge",
        "tab-all",
        "sort-fastest",
        "sort-biggest",
        "sort-favorites",
    };

    private static readonly UIState Leaderboard_LocalView = new UIState(
        "Leaderboard/LocalView",
        setup: root => { },
        navigable: LeaderboardLocalButtons
    );

    private static readonly UIState Leaderboard_GlobalView = new UIState(
        "Leaderboard/GlobalView",
        setup: root =>
        {
            root.Q<Button>("lb-refresh-btn").RemoveFromClassList("lb--hidden");
            root.Q("lb-player-panel").RemoveFromClassList("lb--hidden");
            root.Q<Button>("lb-player-play-btn").RemoveFromClassList("lb--hidden");
        },
        navigable: LeaderboardLocalButtons
            .Concat(new[] { "lb-refresh-btn", "lb-player-play-btn" })
            .ToArray()
    );

    private static readonly UIState Leaderboard_ContextMenu = new UIState(
        "Leaderboard/ContextMenu",
        setup: root =>
        {
            root.Q("lb-context-menu").RemoveFromClassList("lb--hidden");
            root.Q<Button>("ctx-favorite-btn").RemoveFromClassList("lb--hidden");
            root.Q<Button>("ctx-play-btn").RemoveFromClassList("lb--hidden");
        },
        navigable: new[] { "ctx-favorite-btn", "ctx-play-btn", "ctx-delete-btn" },
        background: LeaderboardLocalButtons
    );

    private static readonly UIState Leaderboard_DeleteModal = new UIState(
        "Leaderboard/DeleteModal",
        setup: root =>
        {
            var modal = root.Q("delete-modal");
            modal.style.display = DisplayStyle.Flex;
            modal.Q(className: "modal-overlay").RemoveFromClassList("screen--hidden");
        },
        navigable: new[] { "modal-confirm-btn", "modal-cancel-btn" },
        background: LeaderboardLocalButtons
    );

    private static IEnumerable<UIState> LeaderboardStates()
    {
        yield return Leaderboard_LocalView;
        yield return Leaderboard_GlobalView;
        yield return Leaderboard_ContextMenu;
        yield return Leaderboard_DeleteModal;
    }

    [UnityTest]
    public IEnumerator Leaderboard_StateNavigation(
        [ValueSource(nameof(LeaderboardStates))] UIState state
    )
    {
        var root = SetUpDocument(LeaderboardUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        state.Setup(root);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertStateNavigation(root, state);
    }

    [UnityTest]
    public IEnumerator Leaderboard_AllButtonsCovered()
    {
        var root = SetUpDocument(LeaderboardUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertAllButtonsCovered(root, "Leaderboard", LeaderboardStates().ToArray());
    }

    // ── ReplayHud ───────────────────────────────────────────────────

    private static readonly string[] ReplayAllButtons =
    {
        "exit-btn",
        "controls-toggle-btn",
        "play-pause-btn",
        "speed-btn",
        "highlight-btn",
    };

    private static readonly UIState Replay_ControlsVisible = new UIState(
        "Replay/ControlsVisible",
        setup: root => { },
        navigable: ReplayAllButtons
    );

    private static readonly UIState Replay_ControlsHidden = new UIState(
        "Replay/ControlsHidden",
        setup: root =>
        {
            root.Q("controls-bar").style.display = DisplayStyle.None;
        },
        navigable: new[] { "exit-btn", "controls-toggle-btn" }
    );

    private static IEnumerable<UIState> ReplayHudStates()
    {
        yield return Replay_ControlsVisible;
        yield return Replay_ControlsHidden;
    }

    [UnityTest]
    public IEnumerator ReplayHud_StateNavigation(
        [ValueSource(nameof(ReplayHudStates))] UIState state
    )
    {
        var root = SetUpDocument(ReplayHudUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        state.Setup(root);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertStateNavigation(root, state);
    }

    [UnityTest]
    public IEnumerator ReplayHud_AllButtonsCovered()
    {
        var root = SetUpDocument(ReplayHudUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertAllButtonsCovered(root, "ReplayHud", ReplayHudStates().ToArray());
    }
}
