using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

[TestFixture]
public class CoopHubLayoutTests : UILayoutTestBase
{
    protected const string CoopHubUxmlPath = "Assets/UI/Coop/CoopHubRoot.uxml";

    [UnityTest]
    public IEnumerator CoopHub_DefaultList_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(CoopHubUxmlPath, ratio);
        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var hub = root.Q("coop-hub-root");
        var panelBounds = root.worldBound;
        string ctx = $"CoopHub_Default @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            hub,
            panelBounds,
            ctx,
            warn,
            hub.Q<Button>("back-hub-btn"),
            hub.Q<Button>("host-btn"),
            hub.Q<Button>("join-btn"),
            hub.Q<Button>("hub-refresh-btn"),
            hub.Q<Button>("filter-all"),
            hub.Q<Button>("filter-active"),
            hub.Q<Button>("filter-completed")
        );
    }

    [UnityTest]
    public IEnumerator CoopHub_HostModal_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(CoopHubUxmlPath, ratio);

        // Show host modal
        root.Q("host-modal").RemoveFromClassList("modal--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var modal = root.Q("host-modal");
        var panelBounds = root.worldBound;
        string ctx = $"CoopHub_HostModal @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            modal,
            panelBounds,
            ctx,
            warn,
            modal.Q<Button>("host-submit-btn"),
            modal.Q<Button>("host-cancel-btn")
        );
    }

    [UnityTest]
    public IEnumerator CoopHub_JoinModal_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(CoopHubUxmlPath, ratio);

        // Show join modal
        root.Q("join-modal").RemoveFromClassList("modal--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var modal = root.Q("join-modal");
        var panelBounds = root.worldBound;
        string ctx = $"CoopHub_JoinModal @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            modal,
            panelBounds,
            ctx,
            warn,
            modal.Q<Button>("join-submit-btn"),
            modal.Q<Button>("join-cancel-btn")
        );
    }

    [UnityTest]
    public IEnumerator CoopHub_HostingModal_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(CoopHubUxmlPath, ratio);

        // Show hosting modal
        root.Q("hosting-modal").RemoveFromClassList("modal--hidden");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var modal = root.Q("hosting-modal");
        var panelBounds = root.worldBound;
        string ctx = $"CoopHub_HostingModal @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(modal, panelBounds, ctx, warn, modal.Q<Button>("hosting-cancel-btn"));
    }
}
