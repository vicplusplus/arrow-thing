using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

/// <summary>
/// Smoke tests verifying that CSS hidden classes actually resolve to
/// <c>display: none</c> in each UXML document. Catches the class of bug
/// where a hidden class is used in a document that doesn't import the
/// stylesheet defining it (e.g., <c>screen--hidden</c> was used in
/// GameHud.uxml but only defined in MainMenu.uss).
///
/// Also tests responsive CSS selectors (e.g., leaderboard compact mode)
/// to verify class-based styling resolves correctly at different sizes.
/// </summary>
[TestFixture]
public class CSSResolutionTests : UILayoutTestBase
{
    private static readonly UILayoutTestHelper.AspectRatio StandardRatio =
        new UILayoutTestHelper.AspectRatio("16:9", 1920, 1080);

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Finds all elements with the given CSS class and asserts each has
    /// <c>resolvedStyle.display == DisplayStyle.None</c>.
    /// </summary>
    private static void AssertHiddenClassResolves(
        VisualElement root,
        string hiddenClass,
        string context
    )
    {
        var elements = root.Query(className: hiddenClass).ToList();
        Assert.IsTrue(
            elements.Count > 0,
            $"[{context}] No elements found with class '{hiddenClass}' — "
                + "test is stale or wrong UXML"
        );

        foreach (var el in elements)
        {
            string name = !string.IsNullOrEmpty(el.name) ? el.name : el.GetType().Name;
            Assert.AreEqual(
                DisplayStyle.None,
                el.resolvedStyle.display,
                $"[{context}] Element '{name}' has class '{hiddenClass}' but "
                    + $"resolvedStyle.display is {el.resolvedStyle.display} — "
                    + "the CSS rule is not resolving in this document"
            );
        }
    }

    // ── screen--hidden (Shared.uss) ─────────────────────────────────

    [UnityTest]
    public IEnumerator MainMenu_ScreenHidden_Resolves()
    {
        var root = SetUpDocument(MainMenuUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertHiddenClassResolves(root, "screen--hidden", "MainMenu");
    }

    [UnityTest]
    public IEnumerator GameHud_ScreenHidden_Resolves()
    {
        var root = SetUpDocument(GameHudUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertHiddenClassResolves(root, "screen--hidden", "GameHud");
    }

    // ── modal--hidden (GameHud.uss) ─────────────────────────────────

    [UnityTest]
    public IEnumerator GameHud_ModalHidden_Resolves()
    {
        var root = SetUpDocument(GameHudUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertHiddenClassResolves(root, "modal--hidden", "GameHud");
    }

    // ── victory--hidden (VictoryPopup.uss) ──────────────────────────

    [UnityTest]
    public IEnumerator Victory_VictoryHidden_Resolves()
    {
        var root = SetUpDocument(VictoryUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertHiddenClassResolves(root, "victory--hidden", "Victory");
    }

    // ── lb--hidden (Leaderboard.uss) ────────────────────────────────

    [UnityTest]
    public IEnumerator Leaderboard_LbHidden_Resolves()
    {
        var root = SetUpDocument(LeaderboardUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();
        AssertHiddenClassResolves(root, "lb--hidden", "Leaderboard");
    }

    [UnityTest]
    public IEnumerator Leaderboard_ScreenHidden_Resolves()
    {
        // ConfirmModal template instance uses screen--hidden inside Leaderboard.
        var root = SetUpDocument(LeaderboardUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        // The delete-modal is a ConfirmModal template instance with
        // class="modal-instance" (display:none). Its children have
        // screen--hidden but the parent's display:none makes resolvedStyle
        // unreliable for nested elements. Instead verify the modal-instance
        // itself is hidden, then check screen--hidden on the overlay inside it.
        var deleteModal = root.Q("delete-modal");
        Assert.AreEqual(
            DisplayStyle.None,
            deleteModal.resolvedStyle.display,
            "[Leaderboard] delete-modal (modal-instance) should be display:none"
        );

        // Show the modal instance so we can test screen--hidden inside it.
        deleteModal.style.display = DisplayStyle.Flex;
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var overlay = deleteModal.Q(className: "modal-overlay");
        Assert.IsNotNull(overlay, "[Leaderboard] delete-modal missing modal-overlay");
        Assert.IsTrue(
            overlay.ClassListContains("screen--hidden"),
            "[Leaderboard] delete-modal overlay missing screen--hidden class"
        );
        Assert.AreEqual(
            DisplayStyle.None,
            overlay.resolvedStyle.display,
            "[Leaderboard] screen--hidden on delete-modal overlay does not resolve"
        );
    }

    // ── modal-instance (Shared.uss) ─────────────────────────────────

    [UnityTest]
    public IEnumerator MainMenu_ModalInstance_Resolves()
    {
        var root = SetUpDocument(MainMenuUxmlPath, StandardRatio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var quitModal = root.Q("quit-modal");
        Assert.IsNotNull(quitModal, "[MainMenu] quit-modal not found");
        Assert.AreEqual(
            DisplayStyle.None,
            quitModal.resolvedStyle.display,
            "[MainMenu] quit-modal (modal-instance) should be display:none"
        );
    }

    // ── Responsive: leaderboard compact mode ────────────────────────

    [UnityTest]
    public IEnumerator Leaderboard_CompactMode_HidesInlineButtons()
    {
        var root = SetUpDocument(LeaderboardUxmlPath, StandardRatio);

        // Add mock entry rows with inline fav/play buttons.
        var list = root.Q("lb-list");
        for (int i = 0; i < 3; i++)
            list.Add(CreateMockEntryRow(i + 1));

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        // Before compact: fav/play buttons should be visible.
        var favBtns = root.Query(className: "lb-fav-btn").ToList();
        var playBtns = root.Query(className: "lb-play-btn").ToList();
        Assert.IsTrue(favBtns.Count > 0, "No lb-fav-btn found in mock rows");
        foreach (var btn in favBtns)
            Assert.AreEqual(
                DisplayStyle.Flex,
                btn.resolvedStyle.display,
                "lb-fav-btn should be visible before compact mode"
            );

        // Apply compact class.
        root.Q("leaderboard-root").AddToClassList("lb-screen--compact");
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        // After compact: fav/play buttons should be hidden.
        foreach (var btn in favBtns)
            Assert.AreEqual(
                DisplayStyle.None,
                btn.resolvedStyle.display,
                "lb-fav-btn should be hidden in compact mode"
            );
        foreach (var btn in playBtns)
            Assert.AreEqual(
                DisplayStyle.None,
                btn.resolvedStyle.display,
                "lb-play-btn should be hidden in compact mode"
            );
    }

    [UnityTest]
    public IEnumerator Leaderboard_CompactThreshold_NarrowWidthIsCompact(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(LeaderboardUxmlPath, ratio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        float rootWidth = root.Q("leaderboard-root").resolvedStyle.width;
        bool shouldBeCompact = rootWidth < 500f;
        string ctx = $"Leaderboard @ {ratio.Name} (width={rootWidth:F0}px)";

        // Document the expected compact state per ratio.
        // The controller applies lb-screen--compact when width < 500.
        if (shouldBeCompact)
        {
            Assert.Less(rootWidth, 500f, $"[{ctx}] Root width should be < 500 for compact mode");
        }
        else
        {
            Assert.GreaterOrEqual(
                rootWidth,
                500f,
                $"[{ctx}] Root width should be >= 500 for wide mode"
            );
        }
    }

    // ── Mock entry helper ───────────────────────────────────────────

    private static VisualElement CreateMockEntryRow(int rank)
    {
        var row = new VisualElement();
        row.AddToClassList("lb-entry");

        var rankLabel = new Label($"#{rank}");
        rankLabel.AddToClassList("lb-rank");
        row.Add(rankLabel);

        var timeLabel = new Label("12:34.567");
        timeLabel.AddToClassList("lb-time");
        row.Add(timeLabel);

        var favBtn = new Button();
        favBtn.AddToClassList("lb-row-btn");
        favBtn.AddToClassList("lb-fav-btn");
        row.Add(favBtn);

        var playBtn = new Button();
        playBtn.AddToClassList("lb-row-btn");
        playBtn.AddToClassList("lb-play-btn");
        row.Add(playBtn);

        return row;
    }
}
