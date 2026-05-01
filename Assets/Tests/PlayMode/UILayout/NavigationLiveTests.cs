using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

/// <summary>
/// Runtime-checked navigation coverage. Where <see cref="NavigationCoverageTests"/>
/// asks "is every visible button accounted for in the test author's
/// Navigable claim?" — which can't catch the case PR #154 fixed (a button
/// that's named, visible, claimed-navigable, but never actually added to
/// the live <see cref="FocusNavigator"/>) — this test asks the opposite:
/// "does the live FocusNavigator's wired item set exactly match what the
/// test author claims?"
///
/// <para>The harness is generic across all <see cref="NavigableScene"/>
/// controllers (no per-controller shim required) — see
/// <see cref="NavGraphLiveTestHarness"/>. The cost is that the test
/// instantiates the real controller and runs its real OnEnable, so
/// scenes whose controllers depend on backend / scene-stack state aren't
/// covered here yet. Adding more scenes is purely additive: drop in a
/// new test method, no controller changes.</para>
/// </summary>
[TestFixture]
public class NavigationLiveTests : UILayoutTestBase
{
    private static readonly UILayoutTestHelper.AspectRatio StandardRatio =
        new UILayoutTestHelper.AspectRatio("16:9", 1920, 1080);

    /// <summary>
    /// Looks up the host UIDocument set up by <see cref="UILayoutTestBase.SetUpDocument"/>.
    /// The harness needs it to wire the controller's uiDocument SerializeField.
    /// </summary>
    private UIDocument GetHostDocument()
    {
        // The base class adds the UIDocument to the same _uiHost it owns;
        // grab it back via GameObject.Find since the field is private.
        var host = GameObject.Find("UILayoutTestHost");
        Assert.IsNotNull(host, "UILayoutTestHost not found — base SetUp didn't run?");
        var doc = host.GetComponent<UIDocument>();
        Assert.IsNotNull(doc, "UIDocument not attached — SetUpDocument wasn't called?");
        return doc;
    }

    private static void AssertNavigableMatches(
        string scenarioName,
        FocusNavigator nav,
        params string[] expectedNames
    )
    {
        Assert.IsNotNull(nav, $"[{scenarioName}] FocusNavigator.Active is null after wiring.");
        var actualNames = NavGraphLiveTestHarness.GetNavigableElementNames(nav);

        // Drop unnamed entries before comparing — anonymous VisualElements
        // (e.g. dynamically-built row containers) can legitimately appear
        // in the navigator alongside named buttons. The author claim is
        // always in terms of named UXML buttons.
        var actualNamed = actualNames.Where(n => !string.IsNullOrEmpty(n)).ToList();

        var expectedSet = new System.Collections.Generic.HashSet<string>(expectedNames);
        var actualSet = new System.Collections.Generic.HashSet<string>(actualNamed);

        var missing = expectedSet.Except(actualSet).ToList();
        var unexpected = actualSet.Except(expectedSet).ToList();

        var msg = new System.Text.StringBuilder();
        if (missing.Count > 0)
        {
            msg.AppendLine(
                $"[{scenarioName}] Buttons claimed navigable but missing from the live FocusNavigator:"
            );
            foreach (var n in missing)
                msg.AppendLine("  - " + n);
        }
        if (unexpected.Count > 0)
        {
            msg.AppendLine(
                $"[{scenarioName}] Buttons in the live FocusNavigator but not claimed by the test:"
            );
            foreach (var n in unexpected)
                msg.AppendLine("  - " + n);
        }
        if (msg.Length > 0)
            Assert.Fail(msg.ToString());
    }

    // ── Main Menu Root ───────────────────────────────────────────────

    // Both tests below run the controller's real OnEnable in the PlayMode
    // test environment, which depends on Unity singletons that init via
    // [RuntimeInitializeOnLoadMethod] (LeaderboardManager.Instance,
    // GlobalToast, etc.). The test-playmode CI environment doesn't always
    // bring those up the same way the editor does, so the assertions are
    // currently [Ignore]'d pending an in-editor pass to validate the
    // harness shape and decide which singletons need to be primed in
    // [UnitySetUp]. The harness itself (NavGraphLiveTestHarness) is the
    // load-bearing piece this PR adds — these tests are illustrative.

    [Ignore("Pending in-editor validation; see PR description.")]
    [UnityTest]
    public IEnumerator MainMenu_Root_LiveNavMatchesClaim()
    {
        SetUpDocument(MainMenuUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var host = GameObject.Find("UILayoutTestHost");
        var (nav, _) = NavGraphLiveTestHarness.WireController<MainMenuController>(
            host,
            GetHostDocument()
        );
        yield return null; // let any deferred Q's resolve

        // Same set NavigationCoverageTests claims for MainMenu/Root, minus
        // quit-btn which is hidden on WebGL/mobile by MainMenuController
        // and on desktop test runs is the only platform-conditional entry.
        // The test asserts what the live nav contains regardless of branch:
        // either both quit-btn AND the rest, or just the rest. The script
        // sets that based on Application.platform — we accept either.
        var maybeQuit =
            !UnityEngine.Application.isMobilePlatform
            && UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WebGLPlayer
                ? new[] { "quit-btn" }
                : new string[0];
        var expected = maybeQuit
            .Concat(new[] { "play-btn", "settings-btn", "link-github-btn", "link-discord-btn" })
            .ToArray();
        AssertNavigableMatches("MainMenu/Root", nav, expected);
    }

    // ── Leaderboard LocalView (regression test for PR #154) ──────────

    [Ignore("Pending in-editor validation; see PR description.")]
    [UnityTest]
    public IEnumerator Leaderboard_LocalView_LiveNavMatchesClaim()
    {
        SetUpDocument(LeaderboardUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var host = GameObject.Find("UILayoutTestHost");
        var (nav, _) = NavGraphLiveTestHarness.WireController<LeaderboardScreenController>(
            host,
            GetHostDocument()
        );
        yield return null;

        // Expected = the authored Navigable claim for Leaderboard/LocalView
        // in NavigationCoverageTests. If a button is present in the UXML,
        // claimed navigable, but the controller forgets to add a FocusItem
        // for it (the PR #154 bug — lb-mode-classic and lb-mode-endless
        // had only mouse handlers), this test fails on the missing side.
        AssertNavigableMatches(
            "Leaderboard/LocalView",
            nav,
            "lb-back-btn",
            "lb-local-btn",
            "lb-global-btn",
            "lb-mode-classic",
            "lb-mode-endless",
            "tab-small",
            "tab-medium",
            "tab-large",
            "tab-xlarge",
            "tab-all",
            "sort-fastest",
            "sort-biggest",
            "sort-favorites"
        );
    }
}
